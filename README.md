<div align="center">
  <img src="docs/images/logo.svg" width="80" height="80" alt="SwitchDesk 图标">
  <h1>SwitchDesk</h1>
  <p><strong>让 Switch 的游戏、存档与文件各就其位。</strong></p>
  <p>面向 Atmosphère 的 Switch 游戏、存档与 SD 文件管理工具</p>
  <p>
    <a href="https://github.com/Humboldt-Zh/SwitchDesk/releases/tag/v0.2.5-preview">下载预览版</a> ·
    <a href="docs/INSTALL.md">开始使用</a> ·
    <a href="#界面预览">界面预览</a> ·
    <a href="docs/STATUS.md">验证状态</a> ·
    <a href="docs/BUILD.md">构建</a> ·
    <a href="README.en.md">English</a>
  </p>
  <p>
    <a href="https://github.com/Humboldt-Zh/SwitchDesk/actions/workflows/build.yml"><img src="https://github.com/Humboldt-Zh/SwitchDesk/actions/workflows/build.yml/badge.svg" alt="Build and verify"></a>
    <img src="https://img.shields.io/badge/Windows-x64-2563EB" alt="Windows x64">
    <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
    <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0-blue" alt="GPL-3.0"></a>
    <img src="https://img.shields.io/badge/status-development_preview-E8A23B" alt="开发预览版">
  </p>
</div>

SwitchDesk 在 Windows 电脑上通过 USB 管理 Switch 的游戏、主机存档和 SD 卡文件。打开 Switch 配套程序后，可以浏览主机游戏，将 Switch 存档备份到电脑，并使用文件传输和备份恢复入口；电脑游戏库用于整理安装包与导出内容。

> **已发布预览版**：下载仍为 Windows **0.2.5** + Switch 配套 **0.2.4**，协议均为 **1**。已在一台 OLED 上完成启动、按键和 USB 资料读取。验证环境与功能说明见[验证记录](docs/STATUS.md)和[已知问题](docs/KNOWN-ISSUES.md)。
>
> **当前开发源码：0.3.0-dev.2**。包含独立存档库与时间线、侧栏跨平台存档、NSP 导出修复及随包 SysDVR 资源；本地 **145 项测试、72 张 WPF 渲染**通过。尚未发布新的 Release；迁移开放组合为 **0**，新版真机验收仍待完成。导出修复需要手动替换同一开发包的新 NRO，仅更新电脑端无效。见[开发包范围](docs/PHASE2-DEVELOPMENT-PACK.md)、[修复记录](docs/FIELD-FIXES-2026-11-03.md)和[USB 预览说明](docs/USB-STREAMING.md)。

## 界面预览

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/library-dark.png">
  <img src="docs/images/library-light.png" alt="SwitchDesk 游戏库：左侧导航、中央封面网格与右侧游戏详情">
</picture>

*以上为 SwitchDesk 0.2.5 的实际 WPF 界面，显示从真实主机读取的游戏与封面；使用此前同步的缓存，保留“离线记录”和同步时间。已隐藏主机身份与 emuMMC 指纹，未显示用户或存档资料。这不是现场在线 USB 会话的系统截屏。*

<details>
<summary>查看展开的游戏库</summary>

收起右侧详情后，可以浏览更多真实游戏封面。

![真实主机游戏库，离线缓存](docs/images/library-expanded.png)

</details>

## 可以做什么

- **浏览主机**：查看型号、固件、Atmosphère、系统环境，以及 SD 卡和当前系统用户分区容量。emuMMC 会明确标注。
- **整理游戏库**：合并主机内容与电脑文件；搜索、来源筛选、网格／列表切换，展开本体、更新和已安装 DLC。无法确认的外部文件保留“待识别”。
- **管理 Switch 存档**：按游戏和 Switch 用户读取主机存档，备份为带清单和校验信息的 ZIP；查看历史、连接后自动备份与去重；提供恢复前备份、向主机恢复，以及拖出到电脑资源管理器的入口。
- **传输内容**：SD 文件双向传输、创建目录、重命名和受保护路径检查；游戏安装先集中预检，再进入队列，导出按内容组件选择。
- **保持使用上下文**：断线后保留带时间的离线资料；主题可跟随系统，支持浅／深色与键盘操作。
- **打开画面预览**：调用独立的官方 SysDVR 客户端。已发布 0.2.5 需另行配置；dev.2 开发包附完整运行时与主机安装资源，使用与验收边界见 [USB 预览说明](docs/USB-STREAMING.md)。

更多功能说明、运行条件与验证环境见[能力说明](docs/STATUS.md)。

## 能用它更新 Switch 游戏吗？

**本地更新包通过现有的 USB 安装入口处理，属于开发预览功能。** 需要自行准备与游戏匹配的更新包，通过“安装游戏”选择或拖入；配套程序识别 `Update / Patch` 组件，核对内容和空间后进入安装队列。

已发布 0.2.5 没有自动联网查询或下载游戏更新。当前 dev.2 源码增加公共元数据检查及用户自有来源的下载、校验和安装清单，不提供游戏或更新包下载站。更新包仍需与游戏和目标系统匹配，真实内容安装闭环待验收。见[已发布版更新说明](docs/GAME-UPDATES.md)和[开发版来源配置](docs/CONTENT-SOURCES.md)。

## 开始使用

需要 **Windows x64**、一代 Switch（已运行 Atmosphère）和支持数据传输的 USB 线。当前实际验证电脑为 Windows 11；其他 Windows 环境未纳入兼容承诺。原厂未改机系统不能直接使用配套 NRO。

1. 在仓库的 **[Releases](https://github.com/Humboldt-Zh/SwitchDesk/releases/tag/v0.2.5-preview)** 中下载 Windows 运行包和 Switch 配套包，核对同页 `SHA256SUMS.txt`。Windows 包自带 .NET 运行时，解压后运行 `SwitchDesk.exe`。
2. 将配套程序放到 SD 卡的 `/switch/SwitchDesk/SwitchDesk.nro`；可从相册模式启动基础浏览和 SD 文件管理，存档、安装及导出需要**完整应用模式**。相册模式的有限实测范围见[真机验证记录](docs/HARDWARE-VALIDATION.md)。
3. USB 连接电脑；如未识别，在“连接诊断”核对 WinUSB 驱动和产品名称 **SwitchDesk PC**。

完整安装、更新和驱动步骤见[首次使用](docs/INSTALL.md)。无需连接主机也可以添加电脑游戏目录、校验文件和浏览已有备份。

## 已发布版本验证

以下为已发布 0.2.5 / 0.2.4 的记录。

- **电脑验证**：70 项自动化测试；500 项游戏夹具；浅／深主题、1050／1280／1920 DIP 和 100%／200% WPF 渲染检查。
- **真机验证**：OLED / HOS 20.4.0 / Atmosphère 1.9.4 / emuMMC / 完整应用模式，Switch 配套 0.2.4 启动、X/B 操作、重新打开及一次 USB 资料读取通过。

核对日期：**2026-10-23**。单台设备结果不代表所有型号和固件；WPF 渲染、协议夹具和目标编译分别记录，不能代替实机结果。[详细记录](docs/STATUS.md) · [后续路线](docs/ROADMAP.md)

## 二期开发

当前开发源码为 **0.3.0-dev.2**：独立存档库汇总主机记录和已有电脑备份，点击游戏查看按完整来源隔离的时间线；跨平台存档拥有侧栏首页，选定游戏和用户后自动发现进度文件。二期同时包含双端比较与迁移保护、自有来源的更新 / DLC，以及原生 HOME 精选的准入基础。

2026-11-03 的本地验证为 **145 项测试、72 张 WPF 渲染、Windows 与 NRO 构建及原生 ASan/UBSan 检查通过**；这不是本次 GitHub CI 已通过的声明。只有吸血鬼幸存者与死亡细胞的 Switch 游戏标识已核实，另外四款仍待核对；**六款均未开放跨平台迁移，原生隐藏也保持关闭**。新版导出和 USB 音画仍需实际设备验收；上述旧 Release 下载保持不变。

实现接口与边界见[二期开发说明](docs/PHASE2.md)，逐项进度见[二期任务清单](docs/PHASE2-TASKS.md)，内容来源格式见[来源配置](docs/CONTENT-SOURCES.md)。

## 构建与参与

桌面端使用 C# / WPF / .NET 10，Switch 配套使用 C++ / libnx，并基于固定版本 Sphaira 扩展；本地索引使用 Windows SQLite。

```powershell
# Windows：需要 .NET 10 SDK 和 Python 3
./scripts/build.ps1 -Dotnet dotnet -Python python
```

Switch 端工具链、固定上游源码与完整构建命令见[构建文档](docs/BUILD.md)；协议见[协议说明](docs/PROTOCOL.md)。欢迎提交有步骤的缺陷报告、界面改进和明确标注环境的验证结果，见[贡献指南](CONTRIBUTING.md)。

## 致谢与许可证

Switch 配套基于 [Sphaira 1.0.8](https://github.com/NaGaa95/sphaira)，复用其安装、导出、存档和 USB 实现，并加入 SwitchDesk 管理协议与界面。感谢 [libnx](https://github.com/switchbrew/libnx)、[Atmosphère](https://github.com/Atmosphere-NX/Atmosphere)、[.NET / WPF](https://github.com/dotnet/wpf) 及其贡献者。[SysDVR](https://github.com/exelix11/SysDVR) 通过独立进程调用；0.2.5 未随包分发，dev.2 开发包附官方运行时、主机资源及对应源码和许可证，详见[第三方声明](THIRD-PARTY-NOTICES.md)。

SwitchDesk 以 **[GPL-3.0](LICENSE)** 分发；上游源码、补丁及第三方声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。发布二进制时请一并提供匹配的对应源码。仓库不包含游戏、固件、主机密钥、账户资料或私人存档。本项目与 Nintendo 无隶属关系。
