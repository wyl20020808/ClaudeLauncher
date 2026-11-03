# 交接：Windows 同步 macOS 1.7.19～1.8.0

> 用法：在 Windows 开发机上 `git pull`（dev 与 main）后，把下面「提示词」整段交给那边的 Claude Code 或 Codex。
> 本文件取代 `HANDOFF-windows-1.7.20.md` 的范围；那份的 Windows 特有风险清单仍然有效，本文件引用而不重复。

## 现状（2026-11-03，Mac 侧整理）

- macOS 已发布 `macos-v1.8.0`（`547e3f8`，dev 与 main 同一提交，已推送）。Windows 已发布 `windows-v1.8.4`，只同步到 macOS 1.7.18；1.7.20 交接尚未执行，Windows 自 `be508d9` 后没有新提交。
- 用户这次要求同步并做好测试，**没有授权发布 Windows**。发布前要问用户。
- 版本号按十进制进位（AGENTS.md），Windows 下一个版本建议 1.8.5。

## 需要同步的行为

规格以 `docs/SPEC.md` 为准，约束以 `AGENTS.md` 为准。

| # | 来自 | 内容 | 规格 | Mac 参考 |
| --- | --- | --- | --- | --- |
| 1 | 1.7.19 | 账号表格去内部滚动 | — | `windows/src/LauncherUI.cs` 的 `rowScroll` 嵌在 `accountsScroll` 里，行号先核对 |
| 2 | 1.7.19 | “识别并导入”“同步会话…”旁的说明图标 | — | `resources/i18n/english.strings` 已有键；Windows 用 `{0}` 占位时单独加键 |
| 3 | 1.7.19 / 1.7.20 | 同步会话（分组勾选、预览、进度条、清理副本、标题后缀“（同步 MM/dd HH:mm）”） | SPEC「同步会话」 | `src/SessionSync.m`、`src/ProjectImport.m` 的 `PublishSyncedSession`、`src/main.m` 的 `SessionSyncGuard`、`SessionSyncPicker`、`syncSession:` |
| 4 | 1.7.21 | 新账号同步提示：启动器建的账号就绪（有账号／组织目录且已退出）后询问一次；默认勾选原始会话、不勾选同步副本；询问前只看目录名和进程 | SPEC「同步会话」 | `offerInitialSyncIfReady`、`loadSessionsToSync:`（main.m）、`SyncTargetReady`（ProjectImport.m）、`SessionIsSyncCopy`（SessionSync.m） |
| 5 | 1.7.21 | 用量：只读到周限额时五小时按 0%（估算）显示，悬停说明；同一进程身份且不足 5 小时保留原值 | SPEC「本地界面用量」第 266 行附近 | `4bf9ea4`，`src/UsageInterface.m` |
| 6 | 1.7.22 | 最新版本替换：关联过的会话合并成一行，显示“新增／有更新／已是最新”；有更新的默认勾选，整体替换目标 JSONL，旧版本进回收站；目标比来源新时拒绝 | SPEC「同步会话」写入段 | `LinkSessions`、`MergeLinkedSessions`（SessionSync.m）、`ReplaceSyncedSession`、`SetSyncedSessionDate`（ProjectImport.m）、`targetChanged:`（main.m） |
| 7 | 1.7.23 | 审查修复，全部要带上：不覆盖其他会话的同名记录；记录目录有硬链接等不安全文件时拒绝；查找其他记录只读开头的标识；移动前再核对目标文件；先更新记录再替换正文；放回失败时明确提示旧版本在回收站；版本比较精确到最小时间单位；切换目标只撤销自动勾选；关联逐条保存；文案不写“原会话不变” | `RELEASE-macos-1.7.23.md` | `2619baa` |
| 8 | 1.8.0 | 目标有多套账号／组织目录时，点击同步后读一次 `lastActiveOrg`，只写同名组织目录；读不到、无同名、跨账号重复、不安全一律拒绝；问题在预览前提示；写入 0 条时不提示“会出现在侧栏” | SPEC「同步会话」写入段 | `ResolveSyncTarget`（ProjectImport.m）、`ReadLastActiveOrganization`（UsageHistory.m） |
| 9 | 1.7.20 | 新手教程（8 步）与侧栏“使用教程”入口 | SPEC「新手教程」 | `TutorialOverlay`、`TutorialCardFrame`、`TutorialDocumentText`、`tutorial*`（main.m） |

### Windows 特别注意

- 第 3、6、7 项的 Windows 风险（商店版路径与虚拟化、盘符目录名规则、路径脱敏、不覆盖发布、重解析点、记录字段、版本白名单）见 `HANDOFF-windows-1.7.20.md` 第 1 节。替换用的“移动”要先移到回收站（`Microsoft.VisualBasic.FileIO` 的 `SendToRecycleBin` 或 Shell API），失败要能放回；核对目标未变用卷序列号、文件 ID、大小、修改时间。
- **第 8 项需要先问用户。** AGENTS.md 授权的是 macOS 的 Claude Safe Storage / Claude Key。Windows 上 Cookies 由 `Local State` 里经 DPAPI 保护的密钥加密，Windows 端目前完全没有这段代码。实现前要向用户说明 Windows 的具体做法（只解 `lastActiveOrg`、不碰登录 Cookie、不输出不持久化）并获得同意，同步更新 AGENTS.md。未获同意前，多目录一律拒绝并给出准确提示，不要猜目录。
- 第 9 项的文档和配图要是 Windows 自己的（不能写 ⌘ 快捷键、Finder），配图只用演示数据；文档用 WPF 原生控件，不用 WebBrowser / WebView2。
- 第 5 项改动的是现有用量逻辑，确认不影响趋势、提醒和推荐的现有测试。

## 测试要求（用户重点要求）

自检、离屏预览、真实 GUI 三类分开报告，没做的写“未验证”，不能把自检通过写成 GUI 通过。

自检（`--self-test`，全部用随机临时目录里的合成数据）至少覆盖：

- 清理白名单：JSONL 每行只有 8 个字段加 `custom-title`；记录只有 8 个字段；账号、权限、模型、工具结果、图片不残留。
- Windows 路径脱敏：`C:\Users\<名字>\...` 大小写不同、正反斜杠混用、`..`、重解析点；项目内外判断用真实路径。
- 目录名生成与 200 字符上限拒绝。
- 不覆盖发布：目标已存在时失败；记录失败时只删本次新建的 JSONL。
- 替换：目标比来源新时拒绝；同一秒内的修改能区分；移动前目标变化时停止；记录失败时什么都没变；放回失败时提示正确。
- 同名记录属于其他会话时拒绝；记录目录有硬链接、链接、过大文件时拒绝。
- 关联：逐条保存，中途中断后已写入的仍关联；合并成一行的状态判断；切换目标只撤销自动勾选。
- 标题后缀替换不叠加（含旧的“（副本 …）”“（清理副本）”）。
- 新账号提示：只看目录名和进程，不读会话；只问一次；默认勾选规则。
- 多组织：唯一目录不触发组织读取；多目录时匹配、无匹配、跨账号重复、读取失败四条路径（读取函数用替身注入）。
- 五小时 0% 估算的三种情况：无记录、同进程不足 5 小时、超过 5 小时或进程变化。
- 教程：不启动账号、不写数据、不联网；只自动播放一次；恢复默认不清除记录。
- 离线断言：以上流程不产生任何网络请求、不启动 CLI。

离屏预览：同步窗口（分组、三种状态、预览、进度）、新账号提示、教程每一步、说明图标、浅色／深色／英文。

真实 GUI：只启动少量实例，不碰真实登录数据。**写入任何真实账号目录前先问用户，由用户指定测试账号，并由用户亲自操作 Claude 客户端**确认侧栏显示、历史显示、续聊、替换后的显示。版本白名单只放行实际验证过的 Windows Claude 版本。

---

## 提示词

```
你在 ClaudeLauncher 仓库的 Windows 端（windows/，C# 5 / .NET Framework 4.8 / WPF）工作。macOS 已发布 1.8.0，Windows 已发布 1.8.4（只同步到 macOS 1.7.18）。任务：把 macOS 1.7.19～1.8.0 的功能同步到 Windows，并做好测试。这次用户没有授权发布 Windows，做完先汇报，发布前要问用户。

开始前必须完整阅读：
- AGENTS.md（最高优先级约束，两端都适用，特别是“会话同步例外”“会话同步定位例外”）
- docs/HANDOFF-windows-1.8.0.md（本次清单、Windows 注意事项和测试要求）
- docs/HANDOFF-windows-1.7.20.md 第 1 节（同步会话的 Windows 特有风险，仍然有效）
- docs/SPEC.md「新手教程」「同步会话」「本地界面用量」
- docs/STATUS.md 顶部 macOS 1.7.19～1.8.0 各节，docs/RELEASE-macos-1.7.19.md～RELEASE-macos-1.8.0.md
- windows/README.md、windows/docs/DEVELOPMENT.md
Mac 参考实现只作行为参考，不要照搬平台 API。

硬性约束（摘要，以 AGENTS.md 原文为准）：
- 只做本地操作，绝不向 Claude/Anthropic 及其云端、API、遥测、登录服务发请求，不启动 CLI/浏览器/WebView 间接查询，不读取登录凭据。
- 同步会话只能由用户点击触发并预览确认；来源只读；目标只新增，或在有关联且来源更新时整体替换（旧版本进回收站）；目标账号必须已正常退出；不代发消息。
- Windows 上解密 lastActiveOrg 尚未获授权：实现前先向用户说明做法并取得同意，同步更新 AGENTS.md；未同意前多目录一律拒绝。
- 不修改官方 Claude，不强杀 Claude，不迁移或覆盖固定账号目录。
- 在任何真实账号目录写入测试数据前，必须先问用户，由用户指定测试账号。

第 0 步：git status / git log 盘点本机未推送的提交和未提交改动。git fetch 后把 origin/dev 合入工作分支，不 rebase 已发布提交，不强推。运行 Windows 自检，确认起点全部通过。

第 1 步：低风险项先做完并自检：账号表格去内部滚动、两个说明图标、五小时 0% 估算显示。

第 2 步：同步会话只读调查（商店版路径、配置目录、目录名规则、记录字段、多组织目录结构），不读会话正文。把结论报告给用户，同时说明 Windows 上读取 lastActiveOrg 的具体做法，给出测试账号验证步骤，等用户授权。格式与 Mac 不同就停下，不要猜。

第 3 步：用户授权后，在指定测试账号上用合成会话验证侧栏显示、历史显示、重启后续聊和替换后的显示。客户端操作由用户完成。验证通过后才实现。

第 4 步：实现同步会话全部行为，包括 1.7.21 新账号提示、1.7.22 最新版本替换、1.7.23 全部审查修复、1.8.0 多组织目录定位（未获授权则只做拒绝）。按交接文档「测试要求」补齐自检。

第 5 步：新手教程和“使用教程”入口。Windows 自己的文档和配图，配图只用演示数据，文档不用 WebBrowser 或 WebView2。

第 6 步：同步文档：docs/SPEC.md 相关节的 [Windows 待同步] 改成已实现或写明差异；更新 windows/README.md、windows/docs/USER_GUIDE.md、docs/STATUS.md；新增 docs/RELEASE-windows-1.8.5.md。

第 7 步：验证。按交接文档「测试要求」逐条跑。自检、离屏预览、真实 GUI 分开报告，没做的标为未验证。只启动少量实例，不碰真实登录数据。

做到这里停下来汇报：分支和提交号、每类测试结果和数量、调查结论、与 Mac 的行为差异、未验证项。用户确认后才能发布；发布时把已发布的 macOS 1.8.0 清单和 DMG 原样带入，不要重签。
```
