# 二期开发与准入

更新：2026-11-03。当前开发源码为 `0.3.0-dev.2`；已发布下载仍为 Windows 0.2.5 / Switch 配套 0.2.4，本次不发布新 Release。任务逐项覆盖见 [PHASE2-TASKS.md](PHASE2-TASKS.md)，当前电脑与构建证据见[实测问题修复记录](FIELD-FIXES-2026-11-03.md)。

## 已锁定的产品范围

手动比较后迁移 Steam / Switch 存档；原生 HOME 精选游戏；公共元数据目录配合用户自有 HTTP / NAS / 本地安装包。继续 WPF 与澄空主题；游戏库保留右侧详情，存档采用独立目录和主区域时间线，跨平台存档拥有侧栏首页，所有任务留在连接诊断。USB 帧协议 1、旧快照格式 1 保持兼容。

首批目标是吸血鬼幸存者（1794680）、死亡细胞（588650）、哈迪斯（1145360）、哈迪斯 2（1145350）、空洞骑士（367520）、丝之歌（1030300）。注册适配器不等于开放迁移；`CompatibilityCatalog.Released` 只有通过具体版本、分支、DLC 和游戏内往返检查的规则才能加入。

当前只有吸血鬼幸存者与死亡细胞的 Switch 游戏标识经只读清单核实；另外四款仍待核对，只开放已实现的电脑存档发现与独立备份，不按名称猜测 Switch 对应关系。已核实游戏可选择 Switch 用户并只读发现文件；**迁移开放组合仍为 0，原生隐藏保持关闭**。标识与准入边界见[兼容规则](PHASE2-COMPATIBILITY.md)。

## 当前开发接口

- `CrossSave.cs`：保存时间的含义、内容指纹、配对、共同基线、单边变化和双边冲突；首次配对不按时间猜来源。
- `Phase2State.cs`：独立 `Phase2/state.db`，SQLite WAL / FULL，同步记录用修订号防止旧窗口覆盖新状态；不更改原有 library.db。
- `SteamSaves.cs`：已知有效路径、用户与槽位隔离、Steam BuildID / 分支读取；不选 LastRunBackup、临时文件或修改时间最新的任意文件。
- `CrossSaveDiscovery.cs`：独立的 Switch 游戏身份登记和只读文件发现；发现登记不授予迁移兼容，切换用户或环境后不沿用旧结果。
- `SaveAdapters.cs`：有边界检查的格式解析。Knight 手工解析固定字符串封装，不调用 BinaryFormatter；死亡细胞校验 SHA-1、限额解压、按块处理永久进度；吸血鬼保留数字序列化形式，校验原始紧凑 JSON 的摘要，不猜序列化器。
- `MigrationWorkspace.cs`：生成独立来源、目标和转换副本，准备后才能确认；PC 用同卷原子替换一个槽位，保留覆盖前副本，禁止删除目标后重建的降级路径。
- `Visibility.cs`：精选方案与双端恢复日志契约、入口保护、环境隔离；没有本固件 HOME / 全部软件 / 分组、10 轮恢复和 3 次重启的证据时拒绝应用。当前没有启用原生隐藏能力。
- `ContentCatalog.cs` / `ContentDownloads.cs`：TitleDB 数字版本、索引版本 1、来源目录边界、最多两个下载、强 ETag 续传、SHA-256 发布缓存；目录未知与目录已有版本分别表达。
- `ManagedUpdates.cs`：安装包真实组件复核、拒绝混合包／降级、系统与游戏版本依赖、确认后复查、所有用户存档保护、安装后复读实际版本。
- `ContentCredentials.cs`：HTTP Basic 凭据使用 Windows 凭据管理器，来源配置只保存 GUID 引用；带凭据请求仅允许 HTTPS，禁止越出来源及自动跳转。

## Switch 配套接口

新增能力通过 `device.info.capabilities` 协商，不能仅凭桌面版本启用：

- `saves.inspect`：整个存档文件系统的指纹、可用的 POSIX 提交时间及 commit_id。时间范围不可靠时返回 ObservedOnly；提交时间不是某一槽位的独立游戏时间。
- `restore.atomic.v1`：暂存、全部校验、数据／日志空间预检、PC 保护副本及 SD 原始副本、一次 `fsFsCommit`；没有空间就拒绝，不退回清空后提交或逐文件提交。一次提交的实际中断与供电行为仍由专用测试存档检查。
- `operations.status`：按操作 ID 查询 SD 日志；上次仍处于 writing / installing 时重新核对实际内容，不能直接重复执行。
- `package.dependencies.v1`：真实 CNMT 的应用归属、组件类型、系统与应用最低版本。
- `install.plan.v1`：只接受一个明确所选组件，核对设备、环境、原内容、版本和依赖；显式设置安装策略，不继承 Sphaira 的跳过／降级设置。

HideApplicationRecord / ShowApplicationRecord 仍是候选路线。不会用全局图标隐藏、删除、归档或注销应用记录代替。

## 保护与当前边界

旧快照的设备、环境、游戏、用户和类型校验保留。跨平台结果要从完整目标快照生成，不能修改旧快照身份绕过校验。来源原文件与未选择槽位保留；覆盖前 Recovery 独立保留。未知格式只检查和备份，不执行其中脚本。

吸血鬼的本地有效文件已确认可以包含 `Version: 0`。此字段不能当作 Steam 游戏版本；准入必须另核对安装 BuildID、分支及 Switch 内容数字版本。当前吸血鬼转换字段边界与 Hades Profile 解析仍需双端样本；不会原样复制整个文件冒充已支持。

死亡细胞当前解析器接受文档中的 `DE AD CE 11` 字节头和格式 1；不把文档中展示的魔数直接当作小端整数。永久进度转换重置当前局，保留目标封装、版本和 DLC 标记；MOD、实验分支及不同版本会拒绝。此处是格式实现，不是游戏内兼容承诺。

普通文件来源拒绝重解析路径；OneDrive 等占位／链接存档需要先准备普通本地副本。本工具不更改 Steam Cloud 设置，不自动退出 Steam 或游戏。

手动选择的、尚未登记发现规则的位置可以检查和备份，暂不据此启用迁移；同一个 Steam AppID 存在多个安装记录时也不猜测当前版本。已确认路径需要与账号、槽位及版本组合一起进入准入。

Steam 会话观察根据已观察到的重启、游戏进程与文件指纹变化提示“可能云端替换”，不声称能够确定所有文件变化的原因。新内容、导入内容和覆盖前副本均保留，始终由用户比较后选择；不会与 Steam 循环争抢文件。

## 本机开发验证入口

```powershell
dotnet run --project tests/SwitchDesk.Tests -c Release
dotnet run --project src/SwitchDesk.Tools -c Release -- inspect-local work/private-samples
dotnet run --project src/SwitchDesk.Tools -c Release -- inspect-console work/private-samples
dotnet run --project src/SwitchDesk.Tools -c Release -- catalog-check work/private-samples
# 创建、读回并删除一个随机测试凭据，不读取用户已有凭据。
dotnet run --project src/SwitchDesk.Tools -c Release -- credential-check work/private-samples
# 仅备份选定三款游戏的原始主机数据；没有恢复／安装调用。
dotnet run --project src/SwitchDesk.Tools -c Release -- capture-console work/private-samples
```

工具输出路径必须是本机被忽略目录。报告含本机路径、用户和设备信息，不提交 Git。测试数据使用合成夹具；真实样本只留在私有目录。

本阶段进行电脑夹具、只读本地样本与目标构建检查。不部署 NRO，不执行主机隐藏、游戏安装或存档写回；后续设备准入任务保留在任务清单。

## 来源与许可

- [Stagstation 固定源码](https://github.com/dizzyfrogs/stagstation/blob/475357523442333586ed4c80a96216340903c7ac/converter.js)：Knight 封装参考；MIT，见 `third_party/licenses/Stagstation-MIT.txt`。SwitchDesk 重写边界检查与转换层，未引入 Node 运行依赖。
- [Vampire PC 格式参考](https://github.com/chrisdfennell/vampire-survivors-save-editor)：摘要与不同引擎序列化差异。没有把 PC 存档说明当作 Switch 格式证明。
- [Alive Cells 格式实现](https://github.com/N3rdL0rd/alivecells/blob/main/savetool/savetool.py)、[格式文档](https://n3rdl0rd.github.io/ModDocCE/files/save/)：死亡细胞头、摘要和数据块说明。没有复制带额外使用限制的其他转换站点源码。
- [Hades 官方](https://www.supergiantgames.com/faqs/hades/)、[Hades II 官方](https://www.supergiantgames.com/faqs/hades-ii/)：官方跨平台云功能与本地迁移是独立流程。
- [Steam Cloud](https://partner.steamgames.com/doc/features/cloud)：会话前后同步，不能靠循环覆盖争抢本地文件。
- [libnx 固定 fs 接口](https://github.com/switchbrew/libnx/blob/b156b4774fb2df7b93402169509266da7547fc4c/nx/include/switch/services/fs.h)：额外数据、提交时间、commit_id；没有写入额外时间字段。
- [NS 服务记录](https://switchbrew.org/wiki/NS_services)：隐藏接口仅为候选，效果需单独核对。
- [TitleDB](https://github.com/blawar/titledb)：元数据目录，不提供安装包；SwitchDesk 不接入公网游戏下载站爬虫。
