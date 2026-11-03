"""Prepare the corresponding GPL source tree and a reproducible Sphaira overlay."""
from pathlib import Path
import argparse, hashlib, re, shutil, urllib.request, zipfile, subprocess, sys, os

ROOT = Path(__file__).resolve().parents[1]
def download(url: str, destination: Path):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not destination.exists():
        print(f"Downloading {url}", flush=True)
        with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "SwitchDesk-build/0.1"}), timeout=120) as source, destination.open("wb") as out:
            shutil.copyfileobj(source, out)

def unpack(archive: Path, destination: Path):
    with zipfile.ZipFile(archive) as z:
        prefix = z.namelist()[0].split('/')[0] + '/'
        for entry in z.infolist():
            relative = entry.filename.removeprefix(prefix)
            if not relative or entry.is_dir(): continue
            target = (destination / relative).resolve()
            if not target.is_relative_to(destination.resolve()): raise RuntimeError("Archive path escapes target")
            target.parent.mkdir(parents=True, exist_ok=True)
            with z.open(entry) as source, target.open("wb") as out: shutil.copyfileobj(source, out)

def replace(path: Path, before: str, after: str):
    text = path.read_text(encoding="utf-8")
    if after in text: return
    if text.count(before) != 1: raise RuntimeError(f"Pinned upstream anchor changed: {path}: {before[:60]}")
    path.write_text(text.replace(before, after), encoding="utf-8")

def main():
    subprocess.run([sys.executable, str(ROOT / "scripts/build-info.py")], check=True)
    p = argparse.ArgumentParser(); p.add_argument("--destination", type=Path, default=ROOT / "vendor/sphaira"); p.add_argument("--offline", action="store_true"); args = p.parse_args()
    destination = args.destination.resolve(); archive = ROOT / "third_party/Sphaira-1.0.8-source.zip"
    if not archive.exists():
        if args.offline: raise RuntimeError("Missing pinned source archive")
        download("https://codeload.github.com/NaGaa95/sphaira/zip/338348e74b6d278bc570a9be5373d25197bf6c8d", archive)
    digest_file = archive.with_suffix(archive.suffix + ".sha256")
    if digest_file.exists() and hashlib.sha256(archive.read_bytes()).hexdigest() != digest_file.read_text().strip(): raise RuntimeError("Upstream archive checksum mismatch")
    if not (destination / "sphaira/source/main.cpp").exists(): unpack(archive, destination)
    for folder in ("include", "source"):
        for source in (ROOT / "companion" / folder).rglob("*"):
            if source.is_file():
                target = destination / "sphaira" / folder / source.relative_to(ROOT / "companion" / folder)
                target.parent.mkdir(parents=True, exist_ok=True); shutil.copyfile(source, target)
    replace(destination / "sphaira/CMakeLists.txt", "add_executable(sphaira\n", "add_executable(sphaira\n    source/server.cpp\n")
    replace(destination / "sphaira/CMakeLists.txt", "    source/server.cpp\n", "    source/server.cpp\n    source/management_view.cpp\n")
    replace(destination / "sphaira/source/main.cpp", '#include "app.hpp"', '#include "app.hpp"\n#include "switchdesk/server.hpp"')
    replace(destination / "sphaira/source/main.cpp", "    app->Loop();", "    switchdesk::Launch();\n    app->Loop();")
    replace(destination / "sphaira/source/usb/usbds.cpp", 'usbDsAddUsbStringDescriptor(&iProduct, "Nintendo Switch")', 'usbDsAddUsbStringDescriptor(&iProduct, "SwitchDesk PC")')
    replace(destination / "sphaira/source/ui/progress_box.cpp", '#include "app.hpp"', '#include "app.hpp"\n#include "switchdesk/server.hpp"')
    replace(destination / "sphaira/source/ui/progress_box.cpp", '#include "switchdesk/server.hpp"', '#include "switchdesk/server.hpp"\n#include "switchdesk/management_view.hpp"')
    replace(destination / "sphaira/include/ui/progress_box.hpp", '    bool m_screen_toggle_enabled{};', '    bool m_screen_toggle_enabled{};\n    bool m_switchdesk_management{};')
    replace(destination / "sphaira/source/ui/progress_box.cpp", '    m_pos.w = 770.f;', '    m_switchdesk_management = m_action == "SwitchDesk PC";\n    if (m_switchdesk_management) switchdesk::ConfigureManagement(this);\n    m_pos.w = 770.f;')
    replace(destination / "sphaira/source/ui/progress_box.cpp", 'auto ProgressBox::Draw(NVGcontext* vg, Theme* theme) -> void {', 'auto ProgressBox::Draw(NVGcontext* vg, Theme* theme) -> void {\n    if (m_switchdesk_management) { switchdesk::DrawManagement(vg); return; }')
    replace(destination / "sphaira/source/ui/progress_box.cpp", "    m_size = size;", "    switchdesk::SetProgress(offset, size);\n    m_size = size;")
    replace(destination / "sphaira/include/usb/base.hpp", "#include <memory>", "#include <memory>\n#include <cstdlib>")
    replace(destination / "sphaira/include/usb/base.hpp", "    std::unique_ptr<u8*> m_aligned{};", "    struct AlignedFree { void operator()(u8* ptr) const { std::free(ptr); } };\n    std::unique_ptr<u8, AlignedFree> m_aligned{};")
    replace(destination / "sphaira/source/usb/base.cpp", "#include <cstring>", "#include <cstring>\n#include <malloc.h>")
    replace(destination / "sphaira/source/usb/base.cpp", "    m_aligned = std::make_unique<u8*>(new(std::align_val_t{TRANSFER_ALIGN}) u8[TRANSFER_MAX]);", "    m_aligned.reset(static_cast<u8*>(memalign(TRANSFER_ALIGN, TRANSFER_MAX)));")
    replace(destination / "sphaira/source/usb/base.cpp", "    auto transfer_buf = *m_aligned;", "    auto transfer_buf = m_aligned.get();\n    R_UNLESS(transfer_buf != nullptr, Result_UsbBadBufferAlign);")
    # Preserve previous registered content. Never run upstream's cross-storage
    # deletion before publishing a newly installed content-meta record.
    yati = destination / "sphaira/source/yati/yati.cpp"
    text = yati.read_text(encoding="utf-8")
    text = text.replace("R_TRY(yati->RemoveInstalledNcas(cnmt));", "/* SwitchDesk: retain previously registered content. */")
    text = text.replace("[[maybe_unused]] Result Yati::RemoveInstalledNcas(", "Result Yati::RemoveInstalledNcas(").replace("Result Yati::RemoveInstalledNcas(", "[[maybe_unused]] Result Yati::RemoveInstalledNcas(")
    yati.write_text(text, encoding="utf-8")
    # Managed installs must not inherit another installer's skip/downgrade settings.
    override_flags = ("allow_downgrade", "skip_if_already_installed", "ticket_only", "skip_base", "skip_patch", "skip_addon", "skip_data_patch", "skip_ticket")
    override_header = destination / "sphaira/include/yati/yati.hpp"
    declarations = "".join(f"    std::optional<bool> {flag}{{}};\n" for flag in override_flags)
    replace(override_header, "struct ConfigOverride {\n", "struct ConfigOverride {\n" + declarations)
    for flag in override_flags:
        replace(yati, f"config.{flag} = App::GetApp()->m_{flag}.Get();", f"config.{flag} = override.{flag}.value_or(App::GetApp()->m_{flag}.Get());")
    replace(destination / "sphaira/source/yati/container/xci.cpp", "    R_UNLESS(out.header.magic == HFS0_MAGIC, Result_XciBadMagic);", "    R_UNLESS(out.header.magic == HFS0_MAGIC, Result_XciBadMagic);\n    R_UNLESS(out.header.total_files <= 0x10000 && out.header.string_table_size <= 16 * 1024 * 1024, Result_XciBadMagic);")
    replace(destination / "sphaira/source/yati/container/xci.cpp", "    if (R_SUCCEEDED(rc)) {", "    if (R_SUCCEEDED(rc)) {\n        R_UNLESS(out.hfs0.header.total_files <= 16, Result_XciBadMagic);")
    replace(destination / "sphaira/source/yati/container/xci.cpp", "        out.string_table.emplace_back(string_table.data() + out.file_table[i].name_offset);", "        const auto index = out.file_table[i].name_offset;\n        R_UNLESS(index < string_table.size(), Result_XciBadMagic);\n        const auto end = static_cast<const char*>(std::memchr(string_table.data() + index, '\\0', std::min<std::size_t>(256, string_table.size() - index)));\n        R_UNLESS(end, Result_XciBadMagic);\n        out.string_table.emplace_back(string_table.data() + index, end - string_table.data() - index);")
    replace(destination / "sphaira/source/yati/container/nsp.cpp", "R_UNLESS(std::memchr(name, '\\0', name_size), Result_NspInvalidHeader);", "R_UNLESS(std::memchr(name, '\\0', std::min<std::size_t>(256, name_size)), Result_NspInvalidHeader);")
    replace(destination / "sphaira/source/yati/container/xci.cpp", "#include <cstring>", "#include <cstring>\n#include <algorithm>")
    replace(destination / "sphaira/CMakeLists.txt", "    NAME ${CMAKE_PROJECT_NAME}", "    NAME SwitchDesk")
    cmake = destination / "sphaira/CMakeLists.txt"
    code = cmake.read_text(encoding="utf-8")
    begin = code.index('execute_process(')
    end = code.index('if (DEFINED sphaira_VERSION_OVERRIDE)', begin)
    if 'if (EXISTS "${CMAKE_SOURCE_DIR}/.git")' not in code:
        git_calls = code[begin:end]
        code = code[:begin] + 'if (EXISTS "${CMAKE_SOURCE_DIR}/.git")\n' + git_calls + 'else()\nset(GIT_COMMIT "${SWITCHDESK_SOURCE_ID}")\nset(GIT_BRANCH "source")\nset(GIT_DIRTY "")\nendif()\n\n' + code[end:]
        cmake.write_text(code, encoding="utf-8")
    # Keep this fork's settings/cache separate from an existing Sphaira install.
    for source in (destination / "sphaira").rglob("*"):
        if source.suffix in (".cpp", ".hpp", ".c", ".h"):
            text = source.read_text(encoding="utf-8")
            updated = text.replace("/config/sphaira", "/config/SwitchDesk").replace("/switch/sphaira", "/switch/SwitchDesk")
            if updated != text: source.write_text(updated, encoding="utf-8")
    # Pre-download exact dependency revisions via HTTPS archives, so a git proxy
    # does not prevent FetchContent from preparing the corresponding source.
    pending = [destination / "sphaira/CMakeLists.txt"]
    seen = set(); cache = []
    lock = __import__("json").loads((ROOT / "build-dependencies.lock.json").read_text())
    expected = {item["file"]: item["sha256"] for item in lock.get("sources", [])}
    while pending:
        cmake_file = pending.pop()
        if not cmake_file.exists(): continue
        cmake_text = cmake_file.read_text(encoding="utf-8")
        declarations = re.findall(r"FetchContent_Declare\((\w+)\s+GIT_REPOSITORY https://github.com/([^\s]+?)\.git\s+GIT_TAG ([^\s)]+)", cmake_text)
        for name, repo, ref in declarations:
            if name in seen: continue
            seen.add(name)
            dep = ROOT / "vendor/deps" / name; bundle = ROOT / "vendor/cache" / (name + "-" + ref + ".zip")
            if not bundle.exists() and not args.offline: download(f"https://codeload.github.com/{repo}/zip/{ref}", bundle)
            if bundle.exists() and bundle.name in expected and hashlib.sha256(bundle.read_bytes()).hexdigest() != expected[bundle.name]: raise RuntimeError("Dependency hash mismatch: " + name)
            if not dep.exists():
                if not bundle.exists():
                    if args.offline: raise RuntimeError("Missing pinned dependency archive: " + name)
                    raise RuntimeError("Download did not publish dependency archive: " + name)
                unpack(bundle, dep)
            cmake_path = dep.as_posix()
            if sys.platform == "win32" and os.environ.get("MSYSTEM"):
                cmake_path = subprocess.check_output(["cygpath", "-u", str(dep.resolve())], text=True).strip()
            cache.append(f'set(FETCHCONTENT_SOURCE_DIR_{name.upper()} "{cmake_path}" CACHE PATH "" FORCE)')
            pending.extend([dep / "CMakeLists.txt", dep / "build/cmake/CMakeLists.txt"])
    config = ROOT / "vendor/dependencies.cmake"; config.parent.mkdir(parents=True, exist_ok=True); config.write_text('\n'.join(cache), encoding="utf-8")
    print(f"Prepared {destination}\nCMake preload: {config}")

if __name__ == "__main__": main()
