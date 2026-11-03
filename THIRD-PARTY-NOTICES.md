# 第三方组件

- SwitchDesk 自有 C#、C++、构建脚本及测试以根目录 GPL-3.0 分发。
- Sphaira 1.0.8，仓库 https://github.com/NaGaa95/sphaira ，固定提交 `338348e74b6d278bc570a9be5373d25197bf6c8d`。完整对应源码在 `third_party/Sphaira-1.0.8-source.zip`，保留上游许可证和逐文件版权说明。该项目及其依赖包括其他作者的代码，逐文件声明应一并保留。
- Switch 配套扩展使用 libnx（libnx Authors，ISC）；依赖的源码版本和许可证由对应 devkitPro 包及上游来源提供。
- Sphaira 的依赖版本由其固定 CMakeLists 声明，包括 yyjson、zstd、stb、minIni、libnxtc、NanoVG/deko3d、libpulsar、dr_libs 等。准备脚本按固定版本提取对应源码并保留其 LICENSE；构建 Switch NRO 时必须保留这些说明。
- USB 管道沿用 Sphaira/Atmosphère 的上游实现。本分支修改产品字符串、修复对齐缓冲区所有权，并添加独立管理协议；原始版权声明保留。
- Windows 运行包包含 Microsoft .NET/WPF 运行时；其许可证和第三方说明随 Windows 运行包的 `licenses/` 目录提供。Windows 内置 `winsqlite3.dll` 由系统提供，未重新分发。
- SysDVR 6.3（https://github.com/exelix11/SysDVR，GPL-2.0）通过官方独立客户端进程调用，未将其客户端代码链接进 SwitchDesk 本体。已发布 0.2.5 未附播放器和模块；当前 0.3.0-dev.2 开发包分发官方未修改的完整 Windows 客户端运行时，以及单独供用户安装的主机 sysmodule / SysDVR-conf 资源。固定版本、摘要与来源见 `streaming-dependencies.lock.json`。

dev.2 包内的 `Streaming/SysDVR/Licenses`、`Streaming/SysDVR/Sources` 和 `StreamingSetup` 保留对应许可证、固定源码及来源清单。生成的 SD 资源移除原始开机模式标记，不修改客户端或模块二进制，也不自动安装到主机。SysDVR 对应源码、FFmpeg 对应源码及原始构建配置一并提供；各组件原有许可和逐文件声明保持有效。

SysDVR 随附依赖包括 .NET Runtime 9.0.13（MIT 及第三方声明）、SDL2 / SDL_image（zlib）、libusb（LGPL-2.1）、FFmpeg 与 FFmpeg.AutoGen（包内核对为 LGPL-3.0）、cimgui / Dear ImGui / ImGui.NET（MIT），以及 Noto（SIL OFL 1.1）和旧版 OpenSans（Apache-2.0）字体。可读副本在 `third_party/licenses/SysDVR/`；实际二进制许可核对、源码版本与分发目录见 [USB 预览许可说明](docs/USB-STREAMING.md)。不要用此摘要替代随包许可原文。

品牌图标与旧的原创示例封面随本项目提供。当前 README 的游戏库截图展示用户主机实际读取的游戏封面；相关游戏商标与美术归各自权利人，未单独分发图标素材或游戏文件。原始上游版权和逐文件许可保持有效。


## M0 toolchain compatibility sources

- libnx: https://github.com/switchbrew/libnx at b156b4774fb2df7b93402169509266da7547fc4c. The pinned source archive and upstream license are supplied in third_party.
- newlib/libgloss iosupport branch: https://github.com/R-YaTian/newlib at 8e4d4166b043622b1fe463dbdb8255a05f0d56b2. The pinned source archive contains the upstream license terms. SwitchDesk applies companion/compat/build_argv.c to preserve pointer width and handle empty argument data.
- GCC/libstdc++ runtime components are used by the Switch target; upstream license and runtime exception references: https://gcc.gnu.org/onlinedocs/libstdc++/manual/license.html . Compiler executables are not included in the runtime delivery.

## 对应源码与公开密钥

发布二进制分别附带匹配版本的完整对应源码包，包含 vendor/cache 中的固定依赖源码。主仓库可按锁文件重新准备依赖。third_party/devkitpro-signing-keys.gpg 为 devkitPro 的公开验签密钥环，不包含私钥。可读的上游许可证副本和来源清单见 third_party/licenses/；副本不取代源码内逐文件许可。

## 二期存档格式参考

- Stagstation，MIT，Copyright (c) 2025 AJB；固定提交 `475357523442333586ed4c80a96216340903c7ac`，来源 https://github.com/dizzyfrogs/stagstation 。Knight 封装常量和格式说明供本地 C# 实现参考；许可证在 `third_party/licenses/Stagstation-MIT.txt`，Windows 包内为 `licenses/Stagstation-MIT.txt`。没有引入其 Node / Web 运行环境。
- Vampire Survivors Save Editor、Alive Cells 用于格式说明核对，没有嵌入其 Python 程序；相关来源及适用范围见 `docs/PHASE2.md`。不复制带额外使用限制的转换站点代码。
- TitleDB 为运行时获取的元数据来源（https://github.com/blawar/titledb）；不随包提供游戏、DLC、主机密钥或用户存档。公开仓库的解析测试数据全部为合成夹具。
