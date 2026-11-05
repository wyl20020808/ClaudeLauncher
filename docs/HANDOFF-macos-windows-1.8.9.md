# 交接：macOS 同步 Windows 1.8.7～1.8.9 及之后的界面改动

> 用法：在 Mac 开发机上 `git checkout dev && git pull` 后，把文末「提示词」整段交给那边的 Claude Code。

## 现状（2026-11-05，Windows 侧整理）

- macOS 最新发布 `macos-v1.8.0`；dev 上另有未发布的 `38c60e9`（同步会话改为格式兼容检查）。macOS 下一个版本按十进制进位是 **1.8.1**。
- Windows 已发布到 `windows-v1.8.9`（`4c49116`）。之后的界面改动（本文第二部分）已提交并推送到 **dev，未发布、未打标签、未合并 main**。
- 本次用户只授权把 Windows 改动同步到远端，**没有授权发布任何新版本**。Mac 那边做完也要先问用户再发布。
- Windows 本次验证（scratch 副本中跑 `windows/scripts/build.ps1`）：LauncherTests 366、SyncTests 154、SessionSyncTests 369、TutorialTests 80、`--self-test` 248 全部通过；真实鼠标键盘、高 DPI、客户机未验证。

规格以 `docs/SPEC.md` 为准（Windows 改动已写入，标了 `[Windows]` 或 `[macOS 待同步]`），约束以 `AGENTS.md` 为准。详细过程和证据见 `docs/STATUS.md` 对应小节和 `docs/RELEASE-windows-1.8.7～1.8.9.md`。

## 第一部分：Windows 已发布、macOS 还没有的行为

| # | 来自 | 内容 | 规格／记录 | Windows 参考 |
| --- | --- | --- | --- | --- |
| 1 | 1.8.7 | 同步会话列表以来源账号官方记录为准：读各账号／组织目录下 `local_*.json` 的 `cliSessionId`、`cwd`、`isArchived` 三个字段（其余不保存）；只列未归档记录对应的 JSONL；按记录 `cwd` 归到项目清单里最深的项目（worktree 会话归到所属项目）；没有记录的 JSONL（改模型／回退留下的旧段，见 `priorCliSessionIds`）不列 | SPEC「同步会话」、AGENTS 会话同步例外（已写明，两端适用） | `windows/src/SessionSync.cs` 的 `ActiveSessions`、`List` |
| 2 | 1.8.8 | 某个组织目录读取出错时只跳过该目录，不让整个账号一条都不列（不安全条目整层跳过的规则不变） | SPEC「同步会话」 | `SessionSync.ListWith`、`SyncScan` |
| 3 | 1.8.8 | 问题诊断：只读报告（计数、原因、打码路径），先在窗口展示，保存到启动器日志目录（保留 10 份），可复制、打开文件夹；“提交到 GitHub”只在二次确认后用浏览器打开本项目新建 issue 页（预填，超长只填结论并复制完整报告）。入口：设置 → 关于、同步窗口左下角、“没有可同步的会话”提示 | SPEC「问题诊断」、AGENTS「问题诊断例外」 | `windows/src/SupportReport.cs` |
| 4 | 1.8.9 | 按侧栏分组同步：只读来源账号 `claude_desktop_config.json` 中 `preferences.epitaxyPrefs` 的 `dframe-group-scopes`、`dframe-code-sections`；同步窗口在项目下按分组分层（侧栏顺序，未分组最后），可整组勾选；副本标题加「【分组名】」前缀（不重复加）。**不写任何官方分组数据**（Windows 实测：官方启动时会覆盖本地写入） | SPEC「同步会话」、AGENTS「侧栏分组例外」 | `windows/src/SidebarGroups.cs`、`SessionSync.GroupTitle` |
| 5 | 1.8.9 | 分组预填：同步完成有分组时多一个“打开并建分组”。用 `claude://code/new?q=…&folder=…` 作为启动参数交给目标账号，新建会话并预填分组指令（分组名 + 侧栏会话 ID），**只预填不发送**；q 上限 14336 字、不能以 `/` 开头，超长改为复制到剪贴板。指令文字已在 `resources/i18n/english.strings` | SPEC「同步会话」、AGENTS「分组预填例外」 | `SidebarGroups.Instruction`／`NewSessionLink`／`ValidLink`、`SessionSync.SidebarId`、`LauncherUI.FinishSessionSync` |

### 第一部分 macOS 特别注意

- **第 3、4、5 项在 AGENTS.md 里的授权写的是 Windows**（「问题诊断例外（…Windows）」「侧栏分组例外（…Windows，只读）」「分组预填例外（…Windows）」）。Mac 实现前要先向用户说明 Mac 上读哪些文件、开什么链接，获得同意后把 AGENTS.md 对应条目扩到 macOS。未获同意前只做第 1、2 项。
- 第 4 项：先只读确认 Mac 版 Claude 的 `~/Library/Application Support/Claude*/claude_desktop_config.json` 里分组字段的结构和分区键（Windows 是“账号目录名/组织目录名”），不要假设和 Windows 相同。
- 第 5 项：先确认 Mac 版 Claude 是否注册并支持 `claude://code/new` 与 `q`、`folder` 参数，以及如何把链接交给**指定账号实例**（Windows 是作为启动参数随 `--user-data-dir` 传入；Mac 不能用泛泛的 `open claude://…`，那会交给任意实例）。做不到定向交付就只做“复制到剪贴板”。
- 不读对话正文、标题、凭据；诊断报告路径打码规则在 Mac 上用 `~` 替换用户目录。
- Windows 专属、Mac 不用同步：1.8.6 所有者接受 Administrators 组；Windows Cookie/DPAPI 解 `lastActiveOrg`（Mac 已有 Safe Storage 版本）。

## 第二部分：Windows 1.8.9 之后、已推送 dev 未发布的界面改动

| # | 内容 | 规格 | Windows 参考 |
| --- | --- | --- | --- |
| 6 | 侧栏“账号管理”改为“用量趋势”：只保留趋势图、观察值和轮换建议；移除账号表、搜索筛选、排序、别名编辑、批量路径复制、配置同步入口。已有别名继续用于卡片和趋势卡；持久化页面键仍为 `accounts`，兼容旧默认页与上次页面 | SPEC「账号与日志交互」开头的 2026-11-04 说明、「用量趋势与轮换建议」 | `LauncherUI.cs` |
| 7 | 教程入口移到 设置 → 关于 →「使用教程 / 查看教程」，侧栏底部入口移除；步骤去掉“账号管理”一步；结束后恢复原页面和设置分类 | SPEC「新手教程」（目前标 `[Windows]`） | `LauncherUI.cs`、`windows/src/Tutorial.cs`、`windows/assets/tutorial/` |
| 8 | 整张实例卡片是启动／打开入口（可键盘操作），去掉独立启动／打开按钮；“···”只保留启动／打开、关闭、删除；正常未运行状态文字隐藏 | SPEC 实例卡片一条 | `LauncherUI.cs` |
| 9 | “关闭”：重新扫描唯一官方主进程并核对启动时间后，只发正常关窗请求（不强杀）；Claude 留在托盘时提示从托盘退出 | 同上 | `LauncherUI.cs`；新词条见 `english.strings` 末尾 |
| 10 | 用量显示改为两个紧凑圆环（时钟图标「5-hour」、日历图标「weekly」），从底部顺时针，中间百分比；<60% 绿 `#3C8A68`、60～80% 黄 `#B88619`、>80% 红 `#C64D40`；缺失项整项隐藏；下方本地倒计时（每秒更新，只显示 `3d 5h` 或 `2h 30m`，到期 `0m`，未知格式隐藏）；圆环标题和倒计时固定英文；悬停保留完整观察时间和官方说明 | SPEC「本地界面用量」显示与保存 | `windows/src/Usage.cs` 的 `QuotaColor`、`ResetCountdown` |
| 11 | 卡片背景换成四张 1960×310 无损 PNG（`resources/ui/card-1-hd.png`～`card-4-hd.png`，196∶31，已在仓库）；左侧名称和限额区叠白色渐变（左端 100% 向右到 0%），圆环透明、圆心对齐卡片垂直中心、紧邻名字右侧；暖纸色底、茶色编号、统一悬停／按下／焦点反馈 | SPEC 实例卡片一条 | `LauncherUI.cs`、`windows/scripts/build.ps1` |
| 12 | Windows 11 标题栏用应用纸色和主题文字色（DWM） | SPEC 系统设置 [Windows] | **Windows 专属，Mac 不用做**（Mac 窗口外观按 AppKit 规范自行判断） |

### 第二部分 macOS 特别注意

- 这些是界面方向的改动，Mac 不必像素级照搬；按 AppKit 原生控件和 Mac 习惯实现，保持行为一致（入口位置、可点区域、菜单项、颜色阈值、倒计时格式、缺失隐藏）。
- 第 6 项移除了别名编辑入口，但别名数据要保留并继续显示，不要删除设置里的别名字段。
- 第 10 项倒计时只依据本地观察时间和官方重置说明计算，不新增任何读取或查询；解析失败一律隐藏，不猜。Mac 的重置说明格式先用现有观察记录核对。
- Mac 的教程配图和文档要用 Mac 自己的截图（只用演示数据），不能复用 Windows 图。
- 新卡片 PNG 每张约 1.1～1.4 MB，注意 DMG 体积；可以构建时转码，但不要再先缩小再放大。

## 测试与报告要求

- 改行为先改 SPEC（把 `[macOS 待同步]` 改掉），再改代码，最后更新 STATUS、使用说明。
- 自检（`scripts/build.sh` 的 `--self-test`，全部用临时目录合成数据）至少覆盖：归档不列、无记录 JSONL 不列、worktree 归项目、嵌套项目归最深、单个组织目录出错只跳过该目录；（获授权后）分组读取与顺序、整组勾选、标题前缀不重复、预填链接长度和非法输入拒绝、诊断报告打码；倒计时各种格式与颜色阈值。
- 自检、离屏预览、真实 GUI 分开报告，没做的标“未验证”。只启动少量实例，不碰真实登录数据，不强杀 Claude。
- 在 dev 分支开发；**不发布、不打标签、不合并 main、不生成正式 DMG 推送**，除非用户当次明确要求。

---

## 提示词

```
你在 ClaudeLauncher 仓库的 macOS 端（src/，Objective-C / AppKit）工作，分支 dev。先 git pull。Windows 已发布到 1.8.9，并在 dev 上推送了之后未发布的界面改动；macOS 停在 1.8.0（dev 上另有未发布的格式兼容检查）。任务：把 macOS 同步到 Windows 的当前行为，下一版本号 1.8.1。

开始前完整阅读：
- AGENTS.md（最高优先级约束，两端都适用）
- docs/HANDOFF-macos-windows-1.8.9.md（本次交接的完整清单和注意事项，按它的编号做）
- docs/SPEC.md 中标 [macOS 待同步] 和 [Windows] 的段落
- docs/STATUS.md 顶部到「Windows 1.8.7」为止的记录，docs/RELEASE-windows-1.8.7.md～1.8.9.md
Windows 参考实现在 windows/src/（SessionSync.cs、SidebarGroups.cs、SupportReport.cs、Usage.cs、LauncherUI.cs），只作行为参考，不照搬平台 API。

硬性要求：
1. 先做交接文档第 1、2 项（同步列表以官方记录为准、单目录出错只跳过）。
2. 第 3、4、5 项（问题诊断、侧栏分组只读、分组预填）在 AGENTS.md 里只授权了 Windows。动手前先停下来向我说明 Mac 上要读的具体文件、字段和要打开的链接，以及如何把 claude://code/new 链接定向交给指定账号实例；我同意后再改 AGENTS.md 并实现。做不到定向交付就只做复制到剪贴板。
3. 再做第 6～11 项界面改动，用 AppKit 原生控件，行为与 Windows 一致，第 12 项（Windows 标题栏）不做。
4. 每项：先改 SPEC，再改代码，加合成数据自检，最后更新 STATUS、使用说明。每项完成后跑 scripts/build.sh 和 --self-test。
5. 自检、离屏预览、真实 GUI 分开报告，没做的写“未验证”。不碰真实登录数据，不强杀 Claude，不向 Claude/Anthropic 发请求。
6. 只在 dev 提交并推送。不发布、不打标签、不合并 main、不上传 DMG，除非我当次明确要求。

全部做完后，给我一份简短汇报：完成了哪些编号、哪些等待我授权、测试数字、未验证项。
```
