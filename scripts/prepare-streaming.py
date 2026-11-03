"""Prepare the pinned, self-contained USB streaming backend without changing a Switch.

This downloads only official upstream archives, verifies every archive SHA-256,
and prepares the Windows runtime plus a separately installable SD-card resource.
It never installs drivers, writes an SD card, or reboots a console.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import time
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "streaming-dependencies.lock.json"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def fetch(asset: dict, cache: Path, offline: bool) -> Path:
    target = cache / asset["file"]
    expected = asset["sha256"].lower()
    if target.is_file() and sha256(target) == expected:
        return target
    if offline:
        raise RuntimeError(f"Missing or invalid offline streaming archive: {target.name}")
    if not asset["url"].startswith(("https://github.com/", "https://codeload.github.com/", "https://raw.githubusercontent.com/")):
        raise RuntimeError("Streaming lock contains a non-upstream download URL")
    temporary = target.with_name(target.name + ".partial")
    for attempt in range(6):
        start = temporary.stat().st_size if temporary.exists() else 0
        headers = {"User-Agent": "SwitchDesk-streaming-preparation", "Accept": "application/octet-stream"}
        if start:
            headers["Range"] = f"bytes={start}-"
        try:
            # Start at the canonical URL on each attempt so expired release
            # redirects are never persisted as download configuration.
            request = urllib.request.Request(asset["url"], headers=headers)
            with urllib.request.urlopen(request, timeout=45) as response:
                append = response.status == 206 and response.headers.get("Content-Range", "").startswith(f"bytes {start}-")
                if response.status == 206 and not append:
                    raise RuntimeError("Incorrect upstream Content-Range")
                with temporary.open("ab" if append else "wb") as output:
                    for block in iter(lambda: response.read1(64 * 1024), b""):
                        output.write(block)
            if sha256(temporary) != expected:
                # A complete but mismatched object must not be resumed.
                temporary.unlink(missing_ok=True)
                raise RuntimeError(f"Streaming archive SHA-256 mismatch: {target.name}")
            temporary.replace(target)
            return target
        except Exception as error:
            print(f"Streaming download attempt {attempt + 1}/6: {target.name}: {error}", flush=True)
            if attempt == 5:
                raise
            time.sleep(min(attempt + 1, 3))
    raise AssertionError("unreachable")


def safe_member(name: str) -> None:
    normalized = name.replace("\\", "/")
    path = PurePosixPath(normalized)
    if path.is_absolute() or ".." in path.parts or ":" in normalized or "\0" in normalized:
        raise RuntimeError(f"Unsafe streaming archive path: {name}")


def extract_zip(archive: Path, destination: Path) -> None:
    with zipfile.ZipFile(archive) as source:
        if source.testzip() is not None:
            raise RuntimeError(f"Streaming ZIP CRC failed: {archive.name}")
        for member in source.infolist():
            safe_member(member.filename)
            if (member.external_attr >> 16) & 0o170000 == 0o120000:
                raise RuntimeError("Symlink in streaming setup archive")
        source.extractall(destination)


def extract_sevenzip(archive: Path, destination: Path, extractor: str) -> None:
    # Windows' built-in bsdtar supports 7z; no machine-wide extractor install.
    listing = subprocess.run([extractor, "-tf", str(archive)], check=True,
                             capture_output=True, text=True, encoding="utf-8")
    for name in listing.stdout.splitlines():
        safe_member(name)
    subprocess.run([extractor, "-xf", str(archive), "-C", str(destination)], check=True)
    for item in destination.rglob("*"):
        if item.is_symlink() or not item.resolve().is_relative_to(destination.resolve()):
            raise RuntimeError("Streaming extraction escaped the staging directory")


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def validate_layout(runtime: Path, lock: dict) -> None:
    for relative in lock["runtime"]["requiredFiles"]:
        file = runtime / relative
        if not file.is_file() or file.stat().st_size == 0:
            raise RuntimeError(f"Incomplete SysDVR runtime: {relative}")
    config = json.loads((runtime / "SysDVR-Client.runtimeconfig.json").read_text(encoding="utf-8-sig"))
    frameworks = config.get("runtimeOptions", {}).get("includedFrameworks", [])
    if not any(f.get("name") == "Microsoft.NETCore.App" and f.get("version") == lock["runtime"]["dotnetRuntime"] for f in frameworks):
        raise RuntimeError("SysDVR archive is not the locked self-contained .NET runtime")


def verify_runtime(runtime: Path) -> dict:
    logs = {}
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    for option in ("--version", "--help", "--show-decoders"):
        result = subprocess.run([str(runtime / "SysDVR-Client.exe"), option], cwd=runtime,
                                capture_output=True, text=True, encoding="utf-8", errors="replace",
                                timeout=20, creationflags=flags)
        if result.returncode != 0 or "SysDVR-Client 6.3" not in result.stdout or "531e9c2" not in result.stdout:
            raise RuntimeError(f"Bundled SysDVR headless verification failed: {option}: {result.stdout[-1000:]} {result.stderr[-1000:]}")
        if option == "--show-decoders" and "h264" not in result.stdout:
            raise RuntimeError("Bundled H.264 decoder not available")
        logs[option] = {"exitCode": result.returncode, "output": result.stdout}
    return {"scope": "headless version/help/codec enumeration; no USB or Switch session", "checks": logs}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--cache-dir", type=Path, default=ROOT / "vendor/streaming/cache")
    parser.add_argument("--windows-output", type=Path, default=ROOT / "artifacts/Windows")
    parser.add_argument("--setup-output", type=Path, default=ROOT / "artifacts/StreamingSetup")
    parser.add_argument("--extractor", default=shutil.which("tar") or "tar")
    parser.add_argument("--verify-runtime", action="store_true")
    args = parser.parse_args()
    lock = json.loads(LOCK.read_text(encoding="utf-8-sig"))
    cache = args.cache_dir.resolve(); cache.mkdir(parents=True, exist_ok=True)
    files = {asset["id"]: fetch(asset, cache, args.offline) for asset in lock["assets"]}
    parent = ROOT / "vendor/streaming"; parent.mkdir(parents=True, exist_ok=True)
    stage = parent / (".SysDVR-stage-" + uuid.uuid4().hex)
    stage.mkdir()
    try:
        extract_sevenzip(files["windows"], stage, args.extractor)
        validate_layout(stage, lock)
        licenses = stage / "Licenses"; licenses.mkdir()
        shutil.copytree(ROOT / "third_party/licenses/SysDVR", licenses, dirs_exist_ok=True)
        sources = stage / "Sources"; sources.mkdir()
        for asset in lock["assets"]:
            if asset.get("purpose") == "source":
                shutil.copy2(files[asset["id"]], sources / asset["file"])
        shutil.copy2(LOCK, stage / LOCK.name)
        write_json(stage / "bundle-manifest.json", {
            "name": "SysDVR", "version": lock["version"], "commit": lock["commit"],
            "originalWindowsArchiveSha256": next(x["sha256"] for x in lock["assets"] if x["id"] == "windows"),
            "dotnetRuntime": lock["runtime"]["dotnetRuntime"],
            "files": [{"path": p.relative_to(stage).as_posix(), "size": p.stat().st_size, "sha256": sha256(p)}
                      for p in sorted(stage.rglob("*")) if p.is_file()]
        })
        runtime = parent / "SysDVR"
        # Only copy this component's pinned files; never recursively remove a
        # developer's output directory or change system PATH/settings.
        shutil.copytree(stage, runtime, dirs_exist_ok=True)
        target = args.windows_output.resolve() / "Streaming/SysDVR"
        shutil.copytree(stage, target, dirs_exist_ok=True)
        validate_layout(target, lock)
        setup = args.setup_output.resolve(); setup.mkdir(parents=True, exist_ok=True)
        sd = setup / "SDCard"; sd.mkdir(exist_ok=True)
        extract_zip(files["switch"], sd)
        # No usb/rtsp/tcp marker is the upstream full module's Disabled boot
        # mode. Existing SD installs must change their persisted mode in the
        # official settings app: copying files cannot remove old SD markers.
        config = sd / "config/sysdvr"
        for marker in ("usb", "rtsp", "tcp"):
            (config / marker).unlink(missing_ok=True)
        shutil.copy2(files["switch"], setup / "SysDVR-v6.3-original.zip")
        (setup / "Sources").mkdir(exist_ok=True)
        shutil.copy2(files["sysdvr-source"], setup / "Sources" / files["sysdvr-source"].name)
        shutil.copytree(ROOT / "third_party/licenses/SysDVR", setup / "Licenses", dirs_exist_ok=True)
        shutil.copy2(ROOT / "docs/USB-STREAMING.md", setup / "README-USB-STREAMING.md")
        write_json(setup / "setup-manifest.json", {"version": lock["version"], "commit": lock["commit"],
            "preparedMode": "Disabled for clean installs", "generatedConfigurationChange": "removed usb/rtsp/tcp boot markers; no invented off marker", "existingInstallRequiresOfficialSettings": True, "deployedToConsole": False,
            "files": [{"path": p.relative_to(setup).as_posix(), "size": p.stat().st_size, "sha256": sha256(p)} for p in sorted(sd.rglob("*")) if p.is_file()]})
        bundled_setup = args.windows_output.resolve() / "StreamingSetup"
        if setup != bundled_setup:
            shutil.copytree(setup, bundled_setup, dirs_exist_ok=True)
        if args.verify_runtime:
            write_json(target / "runtime-verification.json", verify_runtime(target))
        print(json.dumps({"prepared": str(runtime), "windowsRuntime": str(target), "switchSetup": str(setup),
                          "version": lock["version"], "commit": lock["commit"], "offline": args.offline,
                          "hardwareTested": False, "files": len(list(target.rglob('*')))}, ensure_ascii=True))
    finally:
        if stage.resolve().is_relative_to(parent.resolve()) and stage.name.startswith(".SysDVR-stage-"):
            shutil.rmtree(stage)


if __name__ == "__main__":
    main()
