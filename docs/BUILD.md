# 构建与源码对应关系

核对日期：2026-10-23。构建脚本不修改系统 PATH。请将本机 SDK 目录放在自己的 shell 环境或被忽略的本地配置中。

## Windows

需要 Windows x64、.NET 10 SDK 与 Python 3。本次本地使用 .NET SDK 10.0.402，`global.json` 允许兼容的 10.0 feature band；运行包内含运行时。

```powershell
./scripts/build.ps1 -Dotnet dotnet -Python python
# 或分别构建与测试
dotnet build src/SwitchDesk.App/SwitchDesk.App.csproj -c Release
dotnet run --project tests/SwitchDesk.Tests/SwitchDesk.Tests.csproj -c Release
```

输出：`artifacts/Windows/`。脚本默认完成测试、发布和离屏 UI 检查。测试程序是有退出码的控制台检查器，请使用 `dotnet run`，不能把仅执行 `dotnet test` 的空结果作为通过。可用 `-TestDirectory <目录>` 指定夹具目录。

本次公开 Windows 包在上述验证后使用以下命令重新发布，去除 PDB 调试引用中的本机目录；应用源码和来源 ID 不变，但 EXE 哈希与此前私下交付的构建不同：

```powershell
dotnet publish src/SwitchDesk.App/SwitchDesk.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/PublicWindows
```

发布包不要附带 PDB、bin/obj 或个人构建日志。仓库 README 的图片在 docs/images；打包文档时应一并保留。

## Switch

需要 devkitPro/devkitA64、deko3d、switch-curl、switch-glm、switch-zlib、switch-mbedtls、switch-cmake，以及 CMake、Ninja、GNU make、pkgconf、Git、Python。`DEVKITPRO` 指向本机安装目录。

```bash
export DEVKITPRO=/opt/devkitpro
# 将 devkitPro 和构建工具加入当前 shell 的 PATH，按实际安装目录调整
export PATH="$DEVKITPRO/tools/bin:$DEVKITPRO/devkitA64/bin:$DEVKITPRO/portlibs/switch/bin:$PATH"
python3 scripts/prepare-companion.py
bash scripts/build-companion.sh
```

准备脚本校验固定上游源码、应用 `companion/` 覆盖与补丁，再准备依赖。`prepare-sdk.py` 会从随附源码构建固定 libnx/newlib 并安装到指定 `DEVKITPRO` 前缀；请使用专用 SDK 前缀，避免影响其他项目。稳定版 libnx 包不足以构建此基线。

本次实际使用 devkitA64 GCC 16.1.0、CMake 4.4.4、Ninja 1.13.2 和 Windows MSYS 环境。SDK 包版本、SHA-256、公开签名均记录在 `build-dependencies.lock.json`。固定源包括 Sphaira `338348e74b6d278bc570a9be5373d25197bf6c8d`、libnx `b156b4774fb2df7b93402169509266da7547fc4c`、newlib `8e4d4166b043622b1fe463dbdb8255a05f0d56b2`。

若使用 Windows SDK 引导脚本：

```powershell
# 需要 Python zstandard 与可用 gpgv；路径必须指向自己的专用安装目录
python scripts/bootstrap-windows-sdk.py --prefix D:/SwitchDeskSDK --gpgv D:/msys64/usr/bin/gpgv.exe
```

`third_party/devkitpro-signing-keys.gpg` 是用于校验 SDK 包的**公开签名密钥环**，不是主机密钥或私钥。完整源包额外带 `vendor/cache/*.zip`，可离线准备 Sphaira 覆盖层：

```bash
python3 scripts/prepare-companion.py --offline
```

离线准备成功不代表已经编译 NRO；编译仍需完整工具链。输出：`artifacts/Switch/switch/SwitchDesk/SwitchDesk.nro`。不承诺容器 `latest` 与本地 SDK 产物完全一致。

## 原生检查与 CI

```bash
g++ -std=c++20 -Wall -Wextra -Wpedantic -Werror -fsanitize=address,undefined -fno-omit-frame-pointer -Icompanion/include companion/tests/native_tests.cpp -o native-tests
./native-tests
g++ -std=c++20 -O1 -g -Wall -Wextra -Wpedantic -Werror -fsanitize=address,undefined -fno-omit-frame-pointer companion/tests/argv_tests.cpp -o argv-tests
./argv-tests
```

GitHub Actions 自动执行 Windows 与上述原生检查；Switch 容器构建仅手动触发，因宿主 SDK 和下载源仍需核对。尚未在 GitHub 执行这些工作流。相应 sanitizer 只覆盖便携逻辑，不能代替 Horizon 实机检查。

## 发布与对应源码

本次二进制维持 Windows 0.2.5 + Switch 0.2.4 配对，不能把旧 NRO 改名为 0.2.5。`build-info.json` 通过实际 `src/`、`companion/`、`scripts/` 和版本／依赖锁生成来源指纹。文档整理不改变这些应用输入。

发布资产分别附上 **0.2.5 和 0.2.4 完整对应源码**，包含当时的自有代码、补丁、构建脚本、固定上游及依赖源码，不含编译器二进制。旧对应源码包沿用原代码，不从主分支倒推。具体哈希见 Release 的 `release-manifest.json` 和 `SHA256SUMS.txt`。

源码仓库不提交 `bin/`、`obj/`、`artifacts/`、`vendor/`、日志或个人数据。`third_party/` 固定源码归档有意保留，便于追溯来源；`vendor/` 可以重新生成。
