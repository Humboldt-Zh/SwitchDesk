# Switch 游戏更新：已发布 0.2.5 的范围

核对日期：2026-10-23。适用：Windows 0.2.5 / Switch 配套 0.2.4，协议 1。

本页保留旧发布版的源码核对与操作边界。**当前开发源码 0.3.0-dev.2** 已增加公共元数据检查、用户自有来源下载校验及管理安装清单，见[二期接口与准入](PHASE2.md)和[来源配置](CONTENT-SOURCES.md)。本次不发布新 Release；真实安装闭环仍待验收，不能把旧版描述或电脑测试视为新版真机通过。

## 结论

**该已发布预览版已实现“识别电脑上的游戏更新包 → USB 安装到 Switch”的路径。**

用户需要自行提供与游戏匹配的更新包，通过已有“安装游戏”入口选择或拖入。主机将 Patch 元数据识别为 Update 组件，并复用内容安装引擎。没有自动联网查询或下载游戏更新的实现。

## 核对到的源码

- `src/SwitchDesk.App/GameOperations.cs` 的 `PickInstallAsync` / `ReviewInstallAsync`：接受 NSP/XCI/NSZ/XCZ，解析组件并显示主机已有的对应内容版本；不是专用的联网更新页面。
- `src/SwitchDesk.Core/Transfers.cs` 的 `InspectAsync` / `InstallAsync`：通过 `package.inspect` 和 `titles.install` 发送本地包，并响应主机的源文件随机读取请求。
- `companion/source/server.cpp` 的 `MetaKind`：将 `NcmContentMetaType_Patch` 映射为 `Update`；`Inspect` 读取 CNMT 的组件和版本；`titles.install` 检查完整应用模式、目标和空间后调用 `yati::InstallFromSource`。
- 固定 Sphaira 1.0.8 提交 `338348e74b6d278bc570a9be5373d25197bf6c8d` 的 `sphaira/source/yati/yati.cpp`：有 Patch 版本比较和内容注册路径。是否跳过同版本、降级等还受主机端配置影响，不能仅凭桌面清单中的“已有”文字保证处理结果。
- `scripts/prepare-companion.py` 保留旧注册内容，跳过上游安装前删除旧内容的步骤；可能遗留旧 NCA，占用及跨存储行为需验证。

本次核对使用随包源码及固定上游实现，没有向主机发送安装／更新请求。完整对应源码随 Release 提供。

## 更新操作检查清单

1. 对专用测试游戏记录本体、更新、存储位置和当前存档备份。
2. 安装匹配的较新更新包，复读主机版本，重启游戏核对启动与存档延续。
3. 检查重复包、旧版本、不同存储位置、空间不足和取消。
4. 验证中断后原游戏可用性、恢复路径与旧内容占用。

NSZ/XCZ 预检目前要求未压缩的 `.cnmt.nca`；安装包格式与目标系统要求需要匹配。代码不主动降低系统版本要求或主密钥代数。当前没有对用户原有游戏和存档进行更新写入试验。
