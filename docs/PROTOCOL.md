# SwitchDesk USB v1

设备端使用 Sphaira 的双向 USB Bulk 接口，USB 产品字符串必须为 `SwitchDesk PC`。WinUSB 主机端先读取描述符，再交换应用协议。管理状态与 SysDVR USB 会话分开运行。

## 帧

16 字节小端头：4 字节 `0x314B4453`（ASCII `SDK1`）、2 字节协议版本 1、2 字节类型、8 字节载荷长度。单帧上限 16 MiB，普通文件块上限 1 MiB。

类型：1 请求 JSON，2 响应 JSON，3 二进制，5 源文件随机读取请求，6 进度 JSON，7 取消会话（空载荷）。

请求为 `{"id":1,"method":"hello","args":{"protocol":1,"client":"SwitchDesk/0.1.0"}}`。

响应成功为 `{"id":1,"ok":true,"result":{...}}`；失败为 `{"id":1,"ok":false,"error":{"code":"...","message":"..."}}`。`result` 可以是对象或数组。对象中出现 `binaryLength` 时，紧接一个长度匹配的二进制帧。

安装或解析期间，设备发送类型 5，载荷为 `{"offset":4294967313,"length":1048576}`；主机验证文件范围后发送类型 3。源文件保持随机访问，不把整个安装包预先传入 SD。

进度为 `{"completed":123,"total":456,"stage":"..."}`，表示当前子传输；`total=0` 为未知总量。所有大小与偏移都用 64 位整数，不能截断为 32 位。

请求按连接串行执行。取消会话会终止连接并清理句柄；传输半包时设备使用有限的数据块超时，随后重新初始化 USB。恢复写回一旦开始会继续完成或返回失败，不支持事务性掉电回滚。

## 查询和文件接口

- `hello`：协议协商；`device.info`：设备信息和能力列表；`device.release`：结束配套管理模式。
- `storages.list`：SD 与当前系统用户分区。emuMMC 环境标识还包含配置路径指纹，避免不同虚拟系统共用备份命名空间。
- `titles.list`：按应用组织的内容；`titles.icon {titleId}`：按需返回图标二进制。
- `saves.list`：Account/Device 存档及主机侧不透明 key。
- `fs.list {path}`、`fs.stat {path}`、`fs.mkdir {path}`、`fs.rename {path,target}`、`fs.delete {path}`。
- `fs.read.open {path}` → `{handle,size}`；`read.chunk {handle,path,offset,length}` → 二进制响应。普通文件和游戏导出使用空 `path`，存档使用相对路径。
- `fs.write.open {path,size}` → `{handle}`；`write.chunk {handle,path,offset}` 后立即跟二进制帧；`fs.write.commit {handle}` 完成临时文件改名。
- `handle.close {handle}` 释放资源，连接结束也释放所有资源。

远端绝对路径以 `/` 开始，不接受 `..`、反斜杠、驱动器或空字节。主机和电脑都执行路径保护。普通文件接口不能修改 Nintendo、emuMMC、Atmosphère、bootloader、配套程序自身及关键启动文件。

## 存档接口

- `saves.read.open {key}` → `{handle}`；`handle.tree {handle}` 返回相对文件和目录；`read.chunk` 读取已挂载存档。
- ZIP 格式版本 1：`manifest.json`，文件位于 `data/<relative-path>`；目录清单单独保存在 manifest 中。清单含身份信息、每文件 SHA-256、大小及内容指纹。
- `saves.restore.begin {key,manifest}`：核对身份、容量和 SD 暂存空间，返回句柄。
- `saves.restore.file {handle,path,size,sha256}` 登记清单内文件；随后使用 `write.chunk`，主机限制顺序偏移和总大小。
- `saves.restore.commit {handle}`：先完整验证暂存文件，再挂载并替换目标内容、提交和逐文件复核。失败时保留已写入阶段的暂存目录与电脑端恢复日志。

电脑端在恢复请求之前，已完整验证输入 ZIP 并生成当前存档的 Recovery 快照。首次恢复必须使用测试存档验收。

## 游戏接口

- `package.inspect {name,size}`：通过随机读取源文件解析容器、NCA 及 CNMT，返回组件及解压后的所需空间。
- `titles.install {name,size,storage}`：再次预检空间，调用上游安装引擎。`storage` 只能是 `SdCard` 或 `BuiltInUser`。
- `titles.export.open {titleId,version,storage}`：通过上游 NSP 构造器打包单个组件，返回 `{handle,size}`，后续使用 `read.chunk`。

UI DTO 定义位于 `src/SwitchDesk.Core/Models.cs`；设备实现位于 `companion/source/server.cpp`。权限和密钥错误返回实际失败，不用模拟数据替代。
