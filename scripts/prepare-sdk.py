"""Prepare the pinned libnx and libgloss revisions required by Sphaira 1.0.8."""
from pathlib import Path
import argparse, hashlib, json, os, shutil, subprocess, tarfile, urllib.request

root = Path(__file__).resolve().parents[1]
lock = json.loads((root / "build-dependencies.lock.json").read_text())
p = argparse.ArgumentParser()
p.add_argument("--source-root", type=Path, default=root / "vendor/sdk")
p.add_argument("--build", action="store_true")
args = p.parse_args()
args.source_root.mkdir(parents=True, exist_ok=True)
sources = {}
for name in ("libnx", "newlib"):
    item = lock[name]; archive = root / item["archive"]
    if not archive.exists():
        archive.parent.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(urllib.request.Request(item["url"], headers={"User-Agent":"SwitchDesk-build/0.2"}), timeout=120) as response, archive.with_suffix(".partial").open("wb") as out: shutil.copyfileobj(response, out)
        archive.with_suffix(".partial").replace(archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != item["sha256"]: raise RuntimeError("SDK source hash mismatch: " + name)
    destination = (args.source_root / (name + "-" + item["commit"][:12])).resolve()
    if not (destination / "Makefile").exists() and not (destination / "build-libgloss-local.sh").exists():
        destination.mkdir(parents=True, exist_ok=True)
        with tarfile.open(archive) as tar:
            for entry in tar:
                relative = "/".join(entry.name.split("/")[1:])
                if not relative: continue
                target = (destination / relative).resolve()
                if not target.is_relative_to(destination): raise RuntimeError("SDK archive path escapes target")
                if entry.isdir(): target.mkdir(parents=True, exist_ok=True)
                elif entry.isfile():
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with tar.extractfile(entry) as source, target.open("wb") as out: shutil.copyfileobj(source, out)
                    target.chmod(entry.mode)
    sources[name] = destination
overlay = (root / lock["newlibOverlay"]).read_bytes()
target = sources["newlib"] / "libgloss/libsysbase/build_argv.c"
if target.read_bytes() != overlay:
    target.write_bytes(overlay)
    for marker in ("built-libgloss", "installed-libgloss"):
        (sources["newlib"] / "libgloss/build" / marker).unlink(missing_ok=True)
if args.build:
    environment = dict(os.environ)
    environment["MAKEFLAGS"] = "-j4 MAKEINFO=true INSTALL_INFO=true"
    subprocess.run(["bash", "./build-libgloss-local.sh"], cwd=sources["newlib"], env=environment, check=True)
    subprocess.run(["make", "install", "-j4"], cwd=sources["libnx"], check=True)
print(json.dumps({name: str(path) for name, path in sources.items()}))
