"""Extract the exact signed Windows SDK packages without changing system installs.
Requires Python 3, zstandard, and gpgv from the same MSYS environment as cygpath.
"""
from pathlib import Path
import argparse, base64, hashlib, json, shutil, subprocess, tarfile, urllib.request
import zstandard

root = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser()
p.add_argument("--prefix", required=True, type=Path)
p.add_argument("--cache", type=Path, default=root / "vendor/sdk-packages")
p.add_argument("--gpgv", default="gpgv")
args = p.parse_args(); args.prefix = args.prefix.resolve()
args.prefix.mkdir(parents=True, exist_ok=True); args.cache.mkdir(parents=True, exist_ok=True)
lock = json.loads((root / "build-dependencies.lock.json").read_text())
keyring = root / lock["keyring"]["archive"]
if hashlib.sha256(keyring.read_bytes()).hexdigest() != lock["keyring"]["sha256"]: raise RuntimeError("Keyring hash mismatch")
def hostpath(path):
    if shutil.which("cygpath"): return subprocess.check_output(["cygpath", "-u", str(path.resolve())], text=True).strip()
    return str(path.resolve())
links = []
for item in lock["sdkPackages"]:
    archive = args.cache / item["url"].split("/")[-1]
    if not archive.exists():
        with urllib.request.urlopen(urllib.request.Request(item["url"], headers={"User-Agent":"SwitchDesk-build/0.2"}), timeout=120) as response, archive.with_suffix(".partial").open("wb") as out: shutil.copyfileobj(response, out)
        archive.with_suffix(".partial").replace(archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != item["sha256"]: raise RuntimeError("Package hash mismatch: " + item["name"])
    signature = archive.with_suffix(archive.suffix + ".sig"); signature.write_bytes(base64.b64decode(item["signature"]))
    subprocess.run([args.gpgv, "--keyring", hostpath(keyring), hostpath(signature), hostpath(archive)], check=True)
    def extract(tar):
        for entry in tar:
            if not entry.name.startswith("opt/devkitpro/"): continue
            target = (args.prefix / entry.name.removeprefix("opt/devkitpro/")).resolve()
            if not target.is_relative_to(args.prefix): raise RuntimeError("Package escapes SDK prefix")
            if entry.isdir(): target.mkdir(parents=True, exist_ok=True)
            elif entry.isfile():
                target.parent.mkdir(parents=True, exist_ok=True)
                with tar.extractfile(entry) as source, target.open("wb") as out: shutil.copyfileobj(source, out)
            elif entry.issym(): links.append((target, (target.parent / entry.linkname).resolve()))
    if archive.suffix == ".zst":
        with archive.open("rb") as raw, zstandard.ZstdDecompressor().stream_reader(raw) as stream, tarfile.open(fileobj=stream, mode="r|") as tar: extract(tar)
    else:
        with tarfile.open(archive) as tar: extract(tar)
    print("Verified and extracted", item["name"], item["version"], flush=True)
for target, source in links:
    if not source.is_relative_to(args.prefix) or not source.is_file(): raise RuntimeError("Invalid SDK symlink")
    shutil.copyfile(source, target)
