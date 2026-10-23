# 第三方组件

- SwitchDesk 自有 C#、C++、构建脚本及测试以根目录 GPL-3.0 分发。
- Sphaira 1.0.8，仓库 https://github.com/NaGaa95/sphaira ，固定提交 `338348e74b6d278bc570a9be5373d25197bf6c8d`。完整对应源码在 `third_party/Sphaira-1.0.8-source.zip`，保留上游许可证和逐文件版权说明。该项目及其依赖包括其他作者的代码，逐文件声明应一并保留。
- Switch 配套扩展使用 libnx（libnx Authors，ISC）；依赖的源码版本和许可证由对应 devkitPro 包及上游来源提供。
- Sphaira 的依赖版本由其固定 CMakeLists 声明，包括 yyjson、zstd、stb、minIni、libnxtc、NanoVG/deko3d、libpulsar、dr_libs 等。准备脚本按固定版本提取对应源码并保留其 LICENSE；构建 Switch NRO 时必须保留这些说明。
- USB 管道沿用 Sphaira/Atmosphère 的上游实现。本分支修改产品字符串、修复对齐缓冲区所有权，并添加独立管理协议；原始版权声明保留。
- Windows 运行包包含 Microsoft .NET/WPF 运行时；其许可证和第三方说明随 Windows 运行包的 `licenses/` 目录提供。Windows 内置 `winsqlite3.dll` 由系统提供，未重新分发。
- SysDVR 6.3（https://github.com/exelix11/SysDVR，GPL-2.0）仅通过官方独立客户端进程调用。本项目未复制、链接或分发其播放器和主机模块二进制。

品牌图标与旧的原创示例封面随本项目提供。当前 README 的游戏库截图展示用户主机实际读取的游戏封面；相关游戏商标与美术归各自权利人，未单独分发图标素材或游戏文件。原始上游版权和逐文件许可保持有效。


## M0 toolchain compatibility sources

- libnx: https://github.com/switchbrew/libnx at b156b4774fb2df7b93402169509266da7547fc4c. The pinned source archive and upstream license are supplied in third_party.
- newlib/libgloss iosupport branch: https://github.com/R-YaTian/newlib at 8e4d4166b043622b1fe463dbdb8255a05f0d56b2. The pinned source archive contains the upstream license terms. SwitchDesk applies companion/compat/build_argv.c to preserve pointer width and handle empty argument data.
- GCC/libstdc++ runtime components are used by the Switch target; upstream license and runtime exception references: https://gcc.gnu.org/onlinedocs/libstdc++/manual/license.html . Compiler executables are not included in the runtime delivery.

## 对应源码与公开密钥

发布二进制分别附带匹配版本的完整对应源码包，包含 vendor/cache 中的固定依赖源码。主仓库可按锁文件重新准备依赖。third_party/devkitpro-signing-keys.gpg 为 devkitPro 的公开验签密钥环，不包含私钥。可读的上游许可证副本和来源清单见 third_party/licenses/；副本不取代源码内逐文件许可。
