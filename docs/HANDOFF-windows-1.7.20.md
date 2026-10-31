# 交接：Windows 同步 macOS 1.7.19～1.7.20

> 用法：在 Windows 开发机上 `git pull`（dev 与 main）后，把下面「提示词」整段交给那边的 Claude Code 或 Codex。

## 现状（2026-10-31，Mac 侧整理）

- macOS 发布到 `macos-v1.7.20`。Windows 已发布版本是 `windows-v1.8.4`，已同步到 macOS 1.7.18。
- Windows 还缺：同步会话、新手教程、账号表格去内部滚动、两个说明图标。
- macOS 1.7.19 删除的“同步到账号”，Windows 从未提供，不用处理。
- 用户这次只要求交接，**没有授权发布 Windows**。发布前要问用户。

## 需要同步的行为

规格以 `docs/SPEC.md` 为准，这里只列要点和 Windows 特有的风险。

### 1. 同步会话（风险最高，先调查再写）

规格：SPEC「同步会话」，约束：AGENTS.md「会话同步例外」。Mac 参考：`src/SessionSync.m`（列表、清理、标题）、`src/ProjectImport.m` 的 `PublishSyncedSession`、`src/main.m` 的 `SessionSyncGuard`、`SessionSyncPicker`、`syncSession:`。

Mac 上已验证的只有这些，**不能推导到 Windows**：

| 项目 | 结论 |
| --- | --- |
| 存储 | `<profile>/claude-code-sessions/<账号UUID>/<组织UUID>/local_<UUID>.json` 记录，通过 `cliSessionId` 指向 `<配置目录>/projects/<目录名>/<cliSessionId>.jsonl` |
| 配置目录 | 01 用主目录 `.claude`，02～10 用 `<profile>/ClaudeCodeConfig` |
| 目录名 | 路径中每个非字母数字的 UTF-16 单元换成 `-`；超过 200 字符时官方改用哈希，启动器拒绝 |
| 写入路线 | 启动器直接写 JSONL 和 8 字段记录，用户打开目标账号后手动发第一条消息。不走官方导入，不调用任何官方接口 |
| Claude 版本 | Mac 只放行 4.4212.0 |

Windows 必须先只读确认（用合成数据或只看结构，不读会话正文）：

- 商店版（MSIX）下记录和 `projects` 的真实位置，配置目录是否被虚拟化。现有的 `Projects.Discover` 和 `ProfileDeletion.Candidates` 已处理候选路径，复用它们。
- 盘符和反斜杠路径的目录名实际是什么（如 `C:\Users\x\proj`），路径大小写如何比较。不要猜，和本机真实目录名核对。
- 记录文件的字段是否和 Mac 相同；SPEC 只确认过 Windows 4.4212.0.0 前四个字段的顺序。
- Windows 版 Claude 能否显示并续聊这样写入的会话。**这一步要写入账号数据，必须先问用户，由用户指定测试账号并亲自操作客户端。** 版本白名单只放行实际验证过的 Windows 版本。

格式或行为不同就停下来报告。

Windows 实现要特别注意：

- 不覆盖发布：同目录临时文件，再用不带替换标志的移动（`MoveFileEx` 不带 `MOVEFILE_REPLACE_EXISTING`，或 `File.Move`）。先发布 JSONL，再发布记录；记录失败时只删除本次新建的 JSONL。每次发布前重新检查目标账号未运行。
- 文件只允许本人访问；重解析点按链接处理并拒绝。
- 脱敏规则要覆盖 Windows 路径：`C:\Users\<名字>\...`、大小写不同、正反斜杠混用。Mac 的正则不能直接照搬。
- 工具说明里的项目内相对路径统一格式，项目外路径省略。判断“在项目内”时用真实路径比较，防止 `..`、大小写和链接绕过。
- 清理必须从白名单重新生成，不能复制后删字段。来源文件只读。
- 全程不启动 CLI、不发请求、不操作官方界面、不代发消息、不读凭据。日志只记账号编号和消息数量。

界面（和 Mac 相同）：

- 项目页“同步会话…”主色按钮，和“打开”同色，旁边有说明图标。
- 窗口按项目分组，默认折叠、默认不选，支持全选、按项目全选、逐条勾选。目标账号一次一个，运行中的不可选。
- 确认前预览将写入的全部文字。生成和写入时显示进度条。
- 标题后缀 `标题（同步 MM/dd HH:mm）`，英文 `标题 (Synced MM/dd HH:mm)`。再次同步时替换旧后缀，含 `（副本 …）`、`（清理副本）`，不叠加。

### 2. 新手教程

规格：SPEC「新手教程」。Mac 参考：`src/main.m` 的 `TutorialOverlay`、`TutorialCardFrame`、`TutorialDocumentText` 和 `tutorial*` 方法。

- 首次打开自动播放一次，升级用户也算首次；开始播放时立即记录。登录保护期间推迟。恢复默认不清除这个记录。
- 8 步：欢迎、启动或切换账号（没有账号时省略）、新建账号、账号管理、项目管理、同步会话、用量趋势、随时回看。
- 遮罩接管全部点击和快捷键，点高亮处等于下一步，不触发控件；不启动账号、不写账号数据、不联网。回车／→ 下一步，← 上一步，Esc 跳过。关闭系统动画时不播放动画。
- 侧栏底部“使用教程”永久入口，可选“软件内播放”或“查看文档教程”。
- 文档用 WPF 原生控件显示，不用 WebBrowser 或 WebView2，不含链接，图片只从安装目录读取。
- `resources/tutorial/guide.{zh,en}.md` 写的是 Mac 操作（⌘1、Finder 等），配图也是 Mac 界面。Windows 要有自己的文档和用演示数据生成的配图，不要用真实账号数据截图。
- 同步会话那一步要等第 1 项完成才能讲；如果第 1 项暂未发布，教程不要提它。

### 3. 账号表格去内部滚动

Mac 1.7.19 去掉了账号表格的内部滚动，因为滚轮会卡在里面。Windows 有相同结构：`windows/src/LauncherUI.cs` 的 `rowScroll`（约 343 行）嵌在 `accountsScroll`（约 354 行）里。行号先核对。改成只有外层滚动，行多时表格变高，确认滚轮全程可用。

### 4. 说明图标

“识别并导入”和“同步会话…”旁加 i 图标，点开显示简短说明。文案和英文译文用 `resources/i18n/english.strings` 里已有的键，Windows 占位符用 `{0}` 时单独加键，不要覆盖 Mac 的键。

---

## 提示词

```
你在 ClaudeLauncher 仓库的 Windows 端（windows/，C# 5 / .NET Framework 4.8 / WPF）工作。macOS 已发布 1.7.20，Windows 已发布 1.8.4。任务：同步 Mac 1.7.19～1.7.20 的改动。这次用户没有授权发布 Windows，做完先汇报，发布前要问用户。

开始前必须完整阅读：
- AGENTS.md（最高优先级约束，两端都适用，特别是“会话同步例外”）
- docs/HANDOFF-windows-1.7.20.md（本次清单和 Windows 特有风险）
- docs/SPEC.md「新手教程」「同步会话」
- docs/STATUS.md 顶部几节、docs/RELEASE-macos-1.7.19.md、docs/RELEASE-macos-1.7.20.md
- windows/README.md、windows/docs/DEVELOPMENT.md
Mac 参考实现在 src/SessionSync.m、src/ProjectImport.m（PublishSyncedSession）、src/main.m（SessionSyncGuard、SessionSyncPicker、TutorialOverlay），只作行为参考，不要照搬平台 API。

硬性约束（摘要，以 AGENTS.md 原文为准）：
- 只做本地操作，绝不向 Claude/Anthropic 及其云端、API、遥测、登录服务发请求，不启动 CLI/浏览器/WebView 间接查询，不读取登录凭据。
- 同步会话只能由用户点击触发并预览确认；来源只读；目标只新增，不覆盖、不合并；目标账号必须已正常退出；不代发消息。
- 不修改官方 Claude，不强杀 Claude，不迁移或覆盖固定账号目录。
- 在任何真实账号目录写入测试数据前，必须先问用户，由用户指定测试账号。不要在没有授权时写入。

第 0 步：git status / git log 盘点本机未推送的提交和未提交改动。git fetch 后把 origin/dev 合入工作分支，不 rebase 已发布提交，不强推。运行 Windows 自检，确认起点全部通过。

第 1 步：账号表格去内部滚动，加两个说明图标。这两项风险低，先做完并自检。

第 2 步：同步会话调查。按交接文档只读确认商店版路径、配置目录、目录名规则和记录字段，不读会话正文。把结果报告给用户，并给出在测试账号上验证的具体步骤，等用户授权。格式不同就停下，不要猜。

第 3 步：用户授权后，在指定的测试账号上用合成会话验证：侧栏显示、历史显示、重启后续聊。客户端操作由用户完成。验证通过后才实现。

第 4 步：实现同步会话。清理逻辑从白名单重新生成，加自检：字段白名单、账号信息不残留、Windows 路径脱敏（大小写、正反斜杠）、项目内外判断、不覆盖发布、记录失败时回退、后缀替换不叠加。版本白名单只放行实际验证过的 Windows Claude 版本。

第 5 步：新手教程和永久入口。Windows 自己的文档和配图，配图只用演示数据。教程不能启动账号、不能写数据、不能联网，文档不用 WebBrowser 或 WebView2。

第 6 步：同步文档。把 docs/SPEC.md 这两节的 Windows 待同步标记改成已实现或写明差异，更新 windows/README.md、windows/docs/USER_GUIDE.md、docs/STATUS.md。新增 docs/RELEASE-windows-1.8.5.md，版本号建议 1.8.5。

第 7 步：验证。自检、离屏预览和真实 GUI 分开报告，没做的标为未验证，不要把测试通过写成 GUI 通过。只启动少量实例，不碰真实登录数据。

做到这里停下来汇报：分支和提交号、测试结果（自检与 GUI 分开）、调查结论、未验证项。用户确认后才能发布；发布必须把当前已发布的 macOS 1.7.20 清单和 DMG 原样带入，不要重签。
```
