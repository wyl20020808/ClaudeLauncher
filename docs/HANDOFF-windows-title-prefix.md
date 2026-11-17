# 交接：Windows 跟上 macOS 1.8.7 副本标题与建组指令（2026-11-17）

macOS 1.8.7 已发布。Windows 目前停在第一轮改动：`SessionSync.WithoutGroup`、`SidebarGroups.InstructionHead`、界面文字、测试和文档已改，但**没有编译、没有跑测试**，也不含 macOS 第二轮（验收修复）的保护。行为以 `docs/SPEC.md`「同步会话 · 侧栏分组」为准，参考实现是 `src/SessionSync.m` 和 `src/main.m`。

## 1. 去前缀只针对旧副本（`windows/src/SessionSync.cs` `WithoutGroup`）

参考 `SessionTitleWithoutGroup`（`src/SessionSync.m`）：

- 标题必须带已知同步后缀（`OldSuffix` 能匹配上），并且以「【分组名】」开头（分组名两端去空白后与来源分组完全一致），才考虑去掉前缀。
- 前缀和后缀之间剩下的文字去掉空白后不能为空，否则原样保留。保留的文字不做 trim。
- 不带同步后缀的标题一律不动。例如「【门店】毛利分析」同步后是「【门店】毛利分析（同步 …）」。

## 2. 来源没有分组时，借用关联里唯一的分组（`Merge` 与调用处）

参考 `MergeLinkedSessions` 和 `SessionLegacyTitleGroup`：

- `Merge` 记录每个版本时，把该版本的分组一并带上（只放内存，不写文件）。
- 传给 `Clean` 的分组按以下顺序决定：
  1. 来源行自己的分组；
  2. 来源没有分组时，用各版本里唯一的非空分组；
  3. 各版本分组互相冲突，或都没有分组时，传 null。
- 这个分组只用来去标题前缀。同步窗口的分组展示、`CleanedSession.Group` 和建组清单仍然只用来源行自己的分组。

## 3. 新版副本标记（防止二次清理用户标题）

参考 `main.m` 的 `RememberPlainSessionTitle`、`SessionPlainTitlesKey` 和 `previewSessionSync`：

- 每条副本**成功**写入（新增或替换）后，在启动器自己的数据里记一条 `账号号:会话ID`。可以放在 `session-links.json` 旁边新建一个文件，也可以加到它的字段里，但不存标题、分组名或正文。读取失败时当作没有标记，本次也不写，免得覆盖原文件。
- 同步时，如果来源的 `账号号:会话ID` 有标记，就传 null 分组，不去前缀。
- 取消预览或写入失败时不加标记，标记不重复。恢复默认设置保留标记，删除诊断数据也不清除它。

## 4. 建组指令换成 macOS 的新文字

- 把 `SidebarGroups.InstructionHead` 改成与 `src/SessionSync.m` 中 `InstructionHead` **逐字相同**的文字，规则 3～6 有新增内容。
- `resources/i18n/english.strings` 里已经有这条新 key 的英文。改完后删掉注释为“下条保留供 Windows 使用”的那条旧 key 及其注释，并确认 macOS 自检仍然通过；没有 Mac 时，在交付说明里请 macOS 端复跑。
- 用 `cmp` 或测试断言确认：两端中文完全一致，英文 key 能精确匹配上。

## 5. 测试（`windows/tests/SessionSyncTests.cs`）

照 `TestSessionTitleMigration` 和 `SessionSync.m` 中 “No group title prefix” 那一段改写，至少覆盖：

- 「原标题」加分组「甲」，结果是「原标题（同步 …）」。
- 「【甲】原标题（同步 10/26 12:00）」加分组「甲」，结果是「原标题（同步 …）」；分组写成「 甲 \n」时结果相同。
- 「【门店】毛利分析」加分组「门店」，标题不变。
- 「【甲】（同步 …）」和「【甲】 \t（同步 …）」加分组「甲」，前缀保留。
- 「【乙】x（同步 …）」加分组「甲」，或分组为 null，标题不变。
- 中英文两种后缀（`（同步 …）`、` (Synced …)`、`（清理副本）`、` (cleaned copy)` 等）都要覆盖。
- 未分组的旧副本回同步：关联里的原会话在「甲」组时，副本被替换后 JSONL 和记录的标题都不带前缀，官方分组数据不变。关联分组冲突时不去前缀。
- 新版副本有标记时，连续同步两次，标题保持原样。
- 指令包含“不能把首批列表当作全集”“无法确认所在分组的先不移动”“不直接读写 Claude 的本地账号配置或数据文件”“授权确认仍由用户完成，不要绕过”，不含“前缀”“改会话标题”；英文中对应的句子也都在。

## 6. 文档

- `docs/SPEC.md`：删掉“Windows 未跟进本轮迁移保护”和“保留 Windows 当前 key”等说法，改成两端一致，并写明 Windows 标记文件的位置。
- `windows/README.md`、`windows/docs/USER_GUIDE.md`、`docs/使用说明.txt`：加 Windows 1.9.7 的说明。
- `docs/STATUS.md`：加一节。

## 完成标准

- `windows/scripts/build.ps1` 全部通过，包括上面新增的测试，然后提交到 dev。
- 版本号定为 1.9.7。是否发布，先问用户。
- 遵守 AGENTS.md：只用合成数据，不读真实账号，不发 Claude 请求。
