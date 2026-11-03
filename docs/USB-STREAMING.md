# USB 单线画面预览

本页记录打包准备、许可和已核对限制。SwitchDesk 内的播放器生命周期与使用说明以实际界面为准。

## 随包内容与自动识别

当前固定官方 **SysDVR v6.3**，源码提交 `531e9c25c4e5a25954eeec677fe439fe812e9b19`。电脑端保留上游独立客户端与完整运行时，路径为：

```text
Streaming/SysDVR/SysDVR-Client.exe
Streaming/SysDVR/runtimes/win-x64/native/
Streaming/SysDVR/runtimes/resources/
Streaming/SysDVR/Licenses/
Streaming/SysDVR/Sources/
StreamingSetup/SDCard/
```

Windows 包使用官方 `SysDVR-Client-Windows-x64-with-framework.7z`，包含自包含 .NET **9.0.13**、SDL2 **2.28.5**、SDL_image、libusb、FFmpeg DLL 与字体/翻译等资源。它不依赖用户另装系统 .NET 或手动下载播放器；不能只复制一个 EXE 而丢掉 DLL、运行时或资源目录。

PC 软件的自动识别使用随包相对路径，不将开发电脑的绝对路径写入公开配置。上游客户端以独立进程运行；打包不等同于主机模块已经安装。

## 构建准备

在项目根目录使用 Python 3 和可读取 7z 的 `tar`（Windows 自带 bsdtar 可用）：

```powershell
python scripts/prepare-streaming.py --windows-output artifacts/Windows --setup-output artifacts/Windows/StreamingSetup --verify-runtime
```

所有下载均从锁文件指定的官方仓库取得，先核对 SHA-256，再解压/复制。缓存放在忽略的 `vendor/streaming/cache`；准备后的运行时位于 `vendor/streaming/SysDVR`。不修改系统 PATH，不安装驱动，不操作 SD 卡或主机。

已有完整缓存时可离线重建：

```powershell
python scripts/prepare-streaming.py --offline --windows-output artifacts/Windows --setup-output artifacts/Windows/StreamingSetup --verify-runtime
```

缺失缓存或摘要不符时离线模式明确失败，不使用不完整的播放器冒充成功。`--verify-runtime` 仅执行官方客户端 `--version`、`--help`、`--show-decoders`；这些检查不会打开 USB 会话或 SDL 播放窗口，不能代替 Switch 实际音画验证。

## 主机资源与切换顺序

`StreamingSetup/SDCard` 包含官方完整版 sysmodule 和 `switch/SysDVR-conf.nro`，需用户通过现有 SD 传输方式准备并按实际环境启动。先备份已有同名 SysDVR 文件；本工具不会自动复制到主机、覆盖系统配置或重启。

官方原始 `SysDVR.zip` 带 RTSP 开机标记。SwitchDesk 生成的 **SDCard 资源移除 `config/sysdvr/usb`、`rtsp`、`tcp` 三种开机标记**：根据固定源码，没有这三个标记即为 Disabled。没有发明 `off` 文件，也没有采用 USB-Only 版本。

**已有安装注意：** 将新文件夹复制到已有 SD 卡，不会删除卡上原来的标记。请在官方 SysDVR-conf 中点 **“停止串流”（Stop streaming，内部为 Disabled / TYPE_MODE_NULL）**，再使用 **“启动时将当前模式设置为默认值”** 保存默认关闭；不要把文件复制完成当作旧配置已被清除。原始官方 ZIP 另行保留用于来源核对，其默认配置不是推荐直接覆盖的生成资源。

使用顺序：

1. 管理文件、存档或安装时，先在 SysDVR-conf 点“停止串流”，再打开 SwitchDesk 配套。
2. 需要投屏时，先结束管理任务并退出配套，在 SysDVR-conf 中切换为 USB，打开支持视频录制的游戏，再启动电脑端预览。
3. 返回管理前，先停止电脑端预览；然后在主机 SysDVR-conf 中点“停止串流”，再打开 SwitchDesk 配套。

SysDVR 的 USB 模式占用主机 `usbDs`。**只关闭电脑播放器不能释放主机端的 USB 模式**，也不能承诺管理与投屏同时进行。驱动检查只针对 SysDVR 自身接口，不自动更换其他 USB 设备的驱动。

## 能力限制与验证边界

固定 v6.3 官方说明：画面为 **720p / 30fps H.264**，音频为 48kHz 16bit 双声道 PCM；只捕获游戏输出，**HOME、系统设置以及 Applet 模式的 homebrew 画面不捕获**。USB 串流用于直连电脑，官方说明不支持底座模式的 USB 串流。

未开启原生录像的游戏可能没有画面。此包不自动安装额外 DVR 补丁，不把黑屏当作已经连接成功，也不声称覆盖所有游戏、所有固件或完整系统镜像。

当前历史真机环境为 OLED / HOS 20.4.0 / Atmosphère 1.9.4 / emuMMC；**新的 USB 音画链路、驱动、持续播放及切回管理仍需在实际设备验证**。电脑 `--help`、解码器枚举、打包完整性和窗口内嵌检查不等同于这条真机链路通过。

## 许可与对应源码

- SysDVR 上游采用 GPL-2.0 许可文本。SwitchDesk 没有把其客户端代码链接进本体，保留为独立原始进程；包内 `Sources` 附固定提交的完整 SysDVR 源码，含原有第三方源码与构建入口。
- 通过实际包内 `avcodec_license()` 核对，FFmpeg DLL 报告 **LGPL version 3 or later**；不是 LGPL-2.1。对应 FFmpeg 为 `n5.1.2-39-g2953c6381a`，源码提交 `2953c6381afc3d52f3c97692228a5120fad4e0dd`，原始构建配置与来源记录随包保留，未修改这些 DLL。
- FFmpeg.AutoGen 5.1.2.1 的许可文本亦为 LGPL-3.0；SDL2、SDL_image 使用 zlib 许可，libusb 1.0.26 使用 LGPL-2.1，cimgui / Dear ImGui / ImGui.NET 使用 MIT，Microsoft .NET Runtime 使用 MIT 并附其第三方声明。原文本在 `Licenses` 中，不用本页摘要替代原许可。
- 字体没有修改。包内嵌入声明显示 Noto 系列为 SIL OFL 1.1；随附旧版 OpenSans 指向 Apache-2.0。其文件内版权/许可元数据与对应文本一并保留。
- 所有二进制、源归档与配置变更可由 `streaming-dependencies.lock.json`、`bundle-manifest.json`、`setup-manifest.json` 追溯；不要把本机日志、设备序列号、存档、密钥或凭据一起打包。

来源：[SysDVR v6.3 发布](https://github.com/exelix11/SysDVR/releases/tag/v6.3)、[固定提交源码](https://github.com/exelix11/SysDVR/tree/531e9c25c4e5a25954eeec677fe439fe812e9b19)、[上游限制](https://github.com/exelix11/SysDVR/blob/531e9c25c4e5a25954eeec677fe439fe812e9b19/readme.md)、[Windows 构建依赖](https://github.com/exelix11/SysDVR/blob/531e9c25c4e5a25954eeec677fe439fe812e9b19/Client/Platform/BuildWindows.bat)。
