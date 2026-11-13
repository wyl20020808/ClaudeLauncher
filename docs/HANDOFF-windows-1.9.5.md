# 交接：Windows 跟上 macOS 1.8.4 会话同步修复（2026-11-13）

## 同步会话：压缩边界接不上时退回边界前的消息

- 原因：新版自动压缩的 `compact_boundary` 行，`logicalParentUuid` 可能指向没写进 JSONL 的消息（`compactMetadata.preservedMessages.allUuids` 里有、文件里没有）。回溯在边界处断开，只剩压缩后的工具调用；压缩前只有一条用户消息时，就报“来源会话没有可同步的对话文字”。真实案例：账号 03 一条自动压缩后跑到限额的会话。
- 现状：Windows `SessionSync.cs` `Clean` 的回溯循环（`at.Parent ?? at.Logical`）有同样问题。
- 要求：当前行是 `type=system`、`subtype=compact_boundary`，且父 ID 在 `byId` 里找不到时，取 `rows` 中该行之前最近的一条 `user`／`assistant` 行继续回溯。参考 `src/SessionSync.m` `CleanSession` 回溯循环。`Row` 需要加 `Subtype` 字段。SPEC「清理」一段已写好，两端一致。
- 测试：构造“首条用户消息 → 助手 → compact_boundary（logicalParentUuid 指向不存在的 ID）→ 压缩摘要 → 工具调用 → 工具结果”的 JSONL，修复前报错，修复后保留首条用户消息。

## 完成标准

- `build.ps1` 全部通过，新增上面的测试；`docs/STATUS.md` 写一节；提交到 dev。是否发布先问用户。
