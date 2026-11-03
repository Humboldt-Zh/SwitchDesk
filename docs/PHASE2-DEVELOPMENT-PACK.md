# 0.3 开发包使用范围

当前分支提供 Windows 自包含开发包与 Switch NRO 目标构建，供代码和电脑端流程评审。公开下载版本仍为 Windows 0.2.5 / Switch 0.2.4。

## 电脑端

解压 Windows 目录后运行 `SwitchDesk.exe`。默认数据仍在当前 Windows 用户的 LocalAppData / SwitchDesk 中，原 library.db 和快照格式继续可读；二期新增数据独立保存在 Phase2 目录。

需要隔离评审时，可在终端传入 `--data-dir` 和一个新的本机目录，避免把测试资料与日常资料混合。不要把真实数据目录放进仓库；Phase2、Backups、ConsoleCache、凭据引用和检查报告均不提交 Git。

可以评审：游戏页的内容 / 存档 / 跨平台页签、双端比较、有效 PC 存档发现、自有来源设置、更新清单、缓存与下载，以及诊断中的任务和中断状态核对。没有主机也可查看已有缓存、添加来源和下载校验。

跨平台迁移按[实际兼容规则](PHASE2-COMPATIBILITY.md)开放。当前组合尚未开放迁移时仍可分别备份；不会自动覆盖另一端。原生精选游戏继续关闭。

## Switch 配套

目录结构为 `switch/SwitchDesk/SwitchDesk.nro`。本轮不自动部署或替换主机文件，保留原来已运行的 0.2.4；需要恢复设备准入时再安排部署与启动检查。更新只涉及 SwitchDesk 自己的文件，不移动游戏、存档、Nintendo 或 emuMMC 目录。

新能力由协议协商启用，旧配套不会获得新写入入口。即使 NRO 构建成功，具体游戏与版本的迁移规则仍以独立准入为准。

## 资料

- [逐项任务与未完成部分](PHASE2-TASKS.md)
- [实现接口与保护边界](PHASE2.md)
- [自有来源格式](CONTENT-SOURCES.md)
- [开发验证记录](PHASE2-VALIDATION.md)

开发包附匹配源码与摘要；不包含实际游戏、私有样本、主机身份、凭据、下载缓存或原始日志。
