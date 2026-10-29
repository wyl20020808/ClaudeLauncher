# 交接：Windows 同步 macOS 1.7.11～1.7.14 功能

> 用法：把下面「提示词」整段交给 Windows 开发机上的 Claude Code。本文接续 [HANDOFF-windows-sync.md](HANDOFF-windows-sync.md)（1.7.10 版）。

## 现状（2026-10-29，Mac 侧整理）

- macOS 已发布到 `macos-v1.7.14`（build 21，目标 `6af3e8e`）；之后 main 上另有一次未发布修复：五小时额度重置后用量读取失效（见任务 C4）。
- Windows 端已按 1.7.10 交接在本地实现，但还没推送：远端 `windows/` 自 `5beb06a` 起无改动，远端只有 `main`、`dev`，Release 仍是 `windows-v1.7.3`。Mac 侧看不到那边做到哪一步、是否发过版。
- 1.7.10 之后 Mac 新增（Windows 交接里没有）：
  - 1.7.11：日志持久化到文件、日志页改版（7 天／24 小时／全部时间筛选、数字页码、级别色块）；用量按账号读取、手动展开官方弹窗时记录一次。
  - 1.7.12：用量卡片只显示观察时间，读取中不清空数值，每次 AX 调用 0.1 秒超时。
  - 1.7.13：实例卡片只显示名称与运行状态；无别名时显示本地 `.claude.json` 的 `oauthAccount.displayName`。
  - 1.7.14：设置页改为顶部分段标签 + 分组卡片行 + 开关。
  - 发布脚本：先经 API 核对草稿附件再转正式；缓存未更新不回滚；回滚时显式恢复上一版 Latest。

---

## 提示词

```
你在 ClaudeLauncher 仓库的 Windows 端（windows/，C# 5 / .NET Framework 4.8 / WPF）工作。你此前已按 docs/HANDOFF-windows-sync.md（1.7.10 交接）在本机实现了一批功能，但尚未推送。macOS 现已发布到 1.7.14，并在 main 上有一次未发布的用量修复。任务分三段：A 先把本机成果同步上去，B 核对 1.7.10 清单，C 补齐 1.7.11～1.7.14 的增量。

开始前必须阅读（远端 main 的最新版本）：
- AGENTS.md（最高优先级约束，两端都适用；新增了「账号显示名例外」）
- docs/SPEC.md（两端行为以它为准）
- docs/STATUS.md 中 macOS 1.7.11～1.7.14 及其后的记录
- docs/USAGE-RESEARCH.md（做 C4 前读）
Mac 参考实现在 src/（main.m、Settings.m、UsageInterface.m），只作行为参考，不要照搬平台 API。

硬性约束（摘要，以 AGENTS.md 原文为准）：
- 管理功能只做本地操作，绝不向 Claude/Anthropic 及其云端、API、遥测、登录服务发请求，不启动 CLI/浏览器/WebView 间接查询，不读取登录凭据。
- 唯一允许的非 Claude 网络是启动器更新：默认关闭、保存开启后只在每次打开启动器时检查一次 + 手动检查；来源只用本项目 GitHub Releases。
- 账号显示名例外：只可在本机读取 Claude Code `.claude.json` 的 `oauthAccount.displayName`，不读其他字段、不持久化、不记录、不发送。
- 日常运行不依赖 Python/Node。保持 C# 5 语法与 .NET 4.8。
- dev 分支开发，验证后合并 main。改行为先改 SPEC，再改代码，最后更新 STATUS、使用说明、windows/README。
- 自检与 GUI 实测分开报告，未做的标「未验证」。不触碰真实登录数据，只启动少量实例。

A. 同步本机成果（先做，做完先汇报再继续）
1. git status / git log 盘点本机所有未推送的提交和未提交改动（列出分支、提交、涉及文件）。未提交的先提交到对应功能分支，不要丢弃任何改动。
2. git fetch origin，把 origin/main 合并进你的工作分支（不要 rebase 已发布的提交，不要强推）。Mac 侧这段时间改过 docs/SPEC.md、docs/STATUS.md、README.md、AGENTS.md、docs/使用说明.txt 和 src/，windows/ 下没动过；冲突大概率只在文档。解决原则：Mac 段落以 origin/main 为准，Windows 段落保留你的；STATUS 两端记录都保留并按时间排列。
3. 合并后 windows 自检全部通过，再推送功能分支并合入 dev、main 推送。推送前先让我确认。
4. 若本机已经发过 windows-v1.7.x/1.8.x 版本，在 STATUS 补记标签、目标提交、Setup 大小与 SHA-256；没发过就写明未发布。

B. 核对 1.7.10 交接（docs/HANDOFF-windows-sync.md 第 1～6 项）
逐项列出：已完成并自检／已完成未实测／部分完成（缺什么）／未做。只补缺口，不要重做已完成的部分。第 5（侧栏登记）、6（用量）两项原本就要求先调查、等授权，确认你没有越过这条线。

C. 1.7.11～1.7.14 增量（每项一个功能分支，自检通过、文档更新后再做下一项）
1. 日志持久化与日志页改版（1.7.11，SPEC「账号与日志交互」）
   - 日志写入 %LOCALAPPDATA%\Claude-MultiLauncher\launcher-log.json（仅当前用户可访问、原子写入、最新在前），重启和更新后保留；超上限删最旧；损坏或格式不符的记录读取时丢弃。清空需确认，只清启动器自己的日志文件。
   - 时间筛选：最近 7 天（默认）／24 小时／1 小时／5 分钟／本次运行／全部时间，与实例、级别、全文搜索组合。
   - 每页 10 条，数字页码（首页、末页、当前页前后各一页，中间省略）+ 上一页／下一页按结果启停；级别以信息／警告／错误色块显示；右侧“共 N 条日志 · 每页显示 10 条”；空结果有说明；导出全部筛选结果。
2. 账号默认名称与实例卡片精简（1.7.13）
   - 无别名时显示本地 Claude Code 用户名：01 读 %USERPROFILE%\.claude.json，02～10 读 <账号数据目录>\ClaudeCodeConfig\.claude.json（先核对 Windows 上实际位置）。只取 oauthAccount.displayName，按别名规则校验（≤24 字、无控制字符）；只缓存在内存，按文件修改时间重读；文件须为当前用户所有的普通文件（非重解析点、≤32 MB）。不保存、不写日志、不导出；取不到显示“账号 NN”。自检用合成文件，不读真实文件。
   - 实例卡片只显示名称（上）和运行状态（左下）；目录与 Code 配置状态移到悬停提示。
3. 设置页布局（1.7.14）
   - 顶部分段标签切换六个分类；每类按小标题分组为卡片行（图标、标题、说明，右侧开关／选项／按钮）和信息提示；布尔项用开关代替复选框。草稿、保存、撤销、恢复默认行为不变。
   - 参考 docs/assets/screenshot-macos.png 与 Mac 离屏截图，不要求像素一致，保持 Windows 原生观感。
4. 本地界面用量（若 Windows 尚未获授权实现，则只把下列内容写进调查报告，不写代码）
   - Mac 1.7.11～1.7.12 与本次修复踩过的坑，Windows 实现或调查都要覆盖：
     a. Electron 网页辅助功能树默认关闭，只看得到原生外壳（Mac 用 AXManualAccessibility；Windows 需确认 UI Automation 下的等价行为，不得改系统设置）。
     b. 多账号：按官方主进程 PID 归属，账号之间、进程重启后不串值。
     c. Code 首页 https://claude.ai/epitaxy 和对话页 /epitaxy/<会话> 都可能有用量按钮，两种页面都要接受。
     d. 五小时窗口重置后、发第一条消息前，按钮不显示五小时，改显示周额度，例如「用量：每周 · 所有模型：11%周三19:00 重置」；「5 小时限额的 71%3:20 重置」格式只在窗口进行中出现。解析与“手动展开时记录一次”的检测都必须两种都认，否则用户点开弹窗也不会更新。按钮只显示周值时，五小时保留上次观察值，不推算为 0%。
     e. 卡片只显示观察时间；读取中保留旧值，不闪成“—”；每次跨进程调用设短超时，Claude 卡顿时启动器不卡。
   - 如涉及读取 Cookies/DPAPI（Mac 的 lastActiveOrg 指纹），必须先写报告并取得我的明确授权。
5. 发布脚本加固（对照 scripts/publish-macos.sh 与 STATUS「发布脚本复核修复」）
   - publish-release.ps1：上传后先经 GitHub API 下载草稿附件逐字节核对（不经 latest/download 缓存），不符则停在草稿、原 Latest 不变；转正式前记下上一版 Latest；latest/download 最多等 10 分钟。
   - 只有内容确认不符才回滚，并显式把上一版设回 Latest；仅缓存未更新则保留发布、提示稍后复核并以非零状态退出。加 -DryRun；真实分支未执行就标未验证。

版本与发布：
- C1～C3 完成后递增 Windows 版本（改 Updates.cs AssemblyVersion/Version/DisplayVersion 与 installer/ClaudeLauncher.iss）。
- 用 publish-release.ps1 只发布 Windows（标签 windows-v<版本>），从当前 Latest（届时的 macos-v1.7.x）原样带入 macOS DMG 与 latest-macos.json（验签 + 核对哈希，不重签）。发布前先问我确认。
- 发布后在 STATUS 记录：标签、目标提交、Setup 大小与 SHA-256、两端清单复核结果、未验证项。

每轮结束汇报：改了什么、自检结果（数量）、GUI 实测了什么、哪些未验证、下一步。
```
