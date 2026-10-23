from pathlib import Path
import argparse, hashlib, json, zipfile

root = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser(); p.add_argument("--output", type=Path, default=root.parent); args = p.parse_args()
args.output.mkdir(parents=True, exist_ok=True)

windows = root / "artifacts/Windows"
if not (windows / "SwitchDesk.exe").exists(): raise SystemExit("Build/publish Windows before packaging")
with zipfile.ZipFile(args.output / "SwitchDesk-Windows-x64.zip", "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for source in sorted(windows.rglob("*")):
        if source.is_file() and source.suffix != ".pdb" and source.relative_to(windows).parts.count("docs") <= 1:
            z.write(source, "SwitchDesk/" + source.relative_to(windows).as_posix())
directories = ["src", "companion", "scripts", "docs", "tests", "third_party", ".github"]
files = ["README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "Directory.Build.props", "global.json", "SwitchDesk.slnx", ".gitignore", "version.json", "build-info.json", "build-dependencies.lock.json"]
with zipfile.ZipFile(args.output / "SwitchDesk-Source.zip", "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for name in files: z.write(root / name, "SwitchDesk/" + name)
    for directory in directories:
        for source in sorted((root / directory).rglob("*")):
            if source.is_file() and not any(part in ("bin", "obj", "__pycache__") for part in source.relative_to(root).parts): z.write(source, "SwitchDesk/" + source.relative_to(root).as_posix())
manifest = []
for name in ("SwitchDesk-Windows-x64.zip", "SwitchDesk-Source.zip"):
    source = args.output / name
    manifest.append({"file": name, "size": source.stat().st_size, "sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
(args.output / "SHA256SUMS.txt").write_text(''.join(f"{row['sha256']}  {row['file']}\n" for row in manifest), encoding="utf-8")
print(json.dumps(manifest, indent=2))
