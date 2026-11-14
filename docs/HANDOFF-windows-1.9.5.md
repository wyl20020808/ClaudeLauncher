# 交接：Windows 跟上 macOS 1.8.4 会话同步修复（2026-11-13）

## 同步会话：压缩边界接不上时退回边界前的消息

- 原因：新版自动压缩的 `compact_boundary` 行，`logicalParentUuid` 可能指向没写进 JSONL 的消息（`compactMetadata.preservedMessages.allUuids` 里有、文件里没有）。回溯在边界处断开，只剩压缩后的工具调用；压缩前只有一条用户消息时，就报“来源会话没有可同步的对话文字”。真实案例：账号 03 一条自动压缩后跑到限额的会话。
- 现状：Windows `SessionSync.cs` `Clean` 的回溯循环（`at.Parent ?? at.Logical`）有同样问题。
- 要求：当前行是 `type=system`、`subtype=compact_boundary`，且父 ID 在 `byId` 里找不到时，取 `rows` 中该行之前最近的一条 `user`／`assistant` 行继续回溯。参考 `src/SessionSync.m` `CleanSession` 回溯循环。`Row` 需要加 `Subtype` 字段。SPEC「清理」一段已写好，两端一致。
- 测试：构造“首条用户消息 → 助手 → compact_boundary（logicalParentUuid 指向不存在的 ID）→ 压缩摘要 → 工具调用 → 工具结果”的 JSONL，修复前报错，修复后保留首条用户消息。

## 官方更新后的重开提示（macOS 1.8.5，Windows 先实测）

- 原因（macOS 本机日志实测）：官方静默更新让空闲实例逐个正常退出，所有实例退出后只以不带 `--user-data-dir` 的方式重启一次，所以只有 01 回来，02～10 像是“自己关掉”。详见 SPEC「启动与激活」和 macOS 1.8.5 的 STATUS。
- 先实测：在 Windows 的 `%APPDATA%\Claude\logs\main.log`（或该版本的实际日志位置）里找 `[stealth-update] Triggering stealth update after idle timeout`，看更新后重启的进程是否带 `--user-data-dir`。行为相同才做下面的移植；不同就把结论写进 SPEC 和 STATUS，不要照抄。
- 要求（行为相同时）：参考 `src/main.m` 的 `UpdateQuits`／`offerUpdateQuitIfReady`。账号运行时记下官方安装的版本，退出后（不含启动器“关闭”请求的那次）一旦版本变了，就记日志、显示窗口内提示，启动器在前台且空闲时逐个账号问一次“重新打开账号 NN／不用了”。点了才走普通启动流程；不自动启动，不读官方日志或账号数据。Windows 读版本的方式按实际安装形态（MSIX／Squirrel）选定后写进 SPEC。
- 测试：同 `TestUpdateQuits`：只读一次版本、关闭请求不计入、版本不可读时不询问、按账号顺序各问一次、在当前版本退出的不问、重新运行后撤销。

## 完成标准

- `build.ps1` 全部通过，新增上面的测试；`docs/STATUS.md` 写一节；提交到 dev。是否发布先问用户。
