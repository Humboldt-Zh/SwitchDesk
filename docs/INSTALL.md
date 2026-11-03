# 首次使用与更新

适用版本：Windows 0.2.5、Switch 配套 0.2.4、协议 1。核对日期：2026-10-23。

本页保留已发布预览版的下载与安装步骤。**当前开发源码为 0.3.0-dev.2，尚未发布新的 Release**；独立存档库、跨平台首页和新版配套使用范围见[开发包说明](PHASE2-DEVELOPMENT-PACK.md)，随包播放器与主机资源见 [USB 预览说明](USB-STREAMING.md)。导出修复需手动替换同一开发包的新 NRO；新功能的真机验收仍待完成，不沿用旧版结果。

## 下载与启动

在 GitHub 仓库右侧找到 **Releases**。本次预览版下载文件应包括 `SwitchDesk-0.2.5-Windows-x64.zip`、`SwitchDesk-0.2.4-Switch.zip`、`SHA256SUMS.txt`，并附有两个版本的对应源码包。不要将 GitHub 自动生成的 Source code ZIP 当作 Windows 程序。

Windows 包解压后运行 `SwitchDesk.exe`，无需另装 .NET。当前只在 Windows 11 x64 验证；尚未签署 Windows 代码签名证书，系统可能要求确认来源。请按同一 Release 的 SHA-256 核对文件，不要全局关闭系统安全检查。

```powershell
Get-FileHash ./SwitchDesk-0.2.5-Windows-x64.zip -Algorithm SHA256
```

若仓库尚未发布 Release，请先参考[构建文档](BUILD.md)，不要从不明来源下载同名程序。

## Switch 端

需要已运行 Atmosphère 的一代 Switch；Lite、续航版和 OLED 是设计目标，目前只有[所列 OLED 环境](STATUS.md)获实机验证。不包含破解、引导、固件或密钥。

1. 使用读卡器或已配置的 DBI MTP 等 SD 文件传输方式，将 `switch/SwitchDesk/SwitchDesk.nro` 复制到 SD 卡同一路径。
2. 可以从相册的 Applet 模式启动 NRO，进行 USB 基础浏览和 SD 文件管理；使用存档、安装和导出时需要完整应用模式。0.2.4 在指定 OLED 环境的相册模式只读实测见 [HW-SD-001](HARDWARE-VALIDATION.md)，不代表这些写入功能已经通过验收。
3. 使用支持数据传输的 USB 线直连电脑，保持配套程序打开。
4. X 查看配套程序连接详情，B 退出。不要在存档写回时强制退出。

只替换 SwitchDesk 自己的 NRO；更新前保存旧 NRO。不要覆盖 `Nintendo`、`emuMMC` 或其他工具目录。当前没有内置在线更新。

## 连接诊断与 WinUSB

配套产品名称应为 **SwitchDesk PC**。检查“连接诊断”中的状态、错误原因和连接事件；任务也在该页面查看。

使用 Windows 设备管理器核对对应的 USB 接口是否绑定 WinUSB。当前沿用 Sphaira 开发接口 `057E:3000`；VID/PID 可能与其他 homebrew 共用，因此还要核对产品名称与协议握手。安装驱动前先退出其他 USB 管理程序，仅针对匹配的接口操作。切换 DBI、Sphaira 或 SysDVR 时可能需要重新核对驱动。

本项目不自动更换系统驱动。Zadig 等第三方驱动工具仅从其官方来源取得；不要对键盘、鼠标、USB Hub 或其他不匹配设备安装驱动。

连接异常时依次核对数据线、完整应用模式、驱动、是否被其他客户端占用及协议版本。掉线后保留离线记录，重连允许重试；未实现跨断线续传。

## 数据保存在哪里

默认在 `%LOCALAPPDATA%/SwitchDesk` 保存设置、SQLite 索引、日志和缓存；备份、电脑游戏库、导出目录可在设置中更改。更改目录不会自动搬移旧文件。

```powershell
# 使用单独的数据目录
./SwitchDesk.exe --data-dir D:/SwitchDeskData
```

存档恢复必须匹配游戏、用户、类型、来源主机及系统环境；恢复前校验并备份到 `Recovery`。写回不是掉电原子事务，请先用测试存档验收，并保留独立备份。自动备份默认保留 10 个不同内容版本，不自动清理 Recovery。

## 安装游戏更新包

需要自行准备与已安装游戏匹配的更新包，并通过“安装游戏”选择或拖入，核对清单中的 Update 组件、主机环境和空间后开始。工具不自动下载更新。当前为开发预览版，请核对游戏与目标系统，并保留独立存档备份。完整范围见 [GAME-UPDATES.md](GAME-UPDATES.md)。

## SysDVR

**已发布 0.2.5 的说明：** 从 [SysDVR 6.3 官方发布页](https://github.com/exelix11/SysDVR/releases/tag/v6.3) 获取客户端与模块，按[上游说明](https://github.com/exelix11/SysDVR/wiki)配置。该旧版未捆绑播放器、模块或驱动；其启动参数与连续音画尚待实测，不能将入口视为已验证串流支持。

**当前 dev.2 开发包：** 已包含完整官方客户端运行时和单独供用户安装的 SD 资源，不自动部署模块、修改驱动或重启主机。管理与投屏仍需按顺序切换；只关闭电脑播放器不能停止主机 USB 串流。请使用 [USB 预览说明](USB-STREAMING.md)中的配置和切回步骤，真实音画链路仍待验收。
