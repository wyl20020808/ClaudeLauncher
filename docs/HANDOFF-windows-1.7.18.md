# 交接：Windows 同步 macOS 1.7.18 并发布

> 用法：在 Windows 开发机上 `git pull`（dev 与 main）后，把下面「提示词」整段交给那边的 Claude Code 或 Codex。

## 现状（2026-10-30，Mac 侧整理）

- macOS 已发布 `macos-v1.7.18`。Windows 已发布版本停在 `windows-v1.8.3`，Mac 的 Release 原样带入其清单与 Setup。
- 仓库 `windows/` 里已有接手 macOS 1.7.17 的六批改动（`8be8619`），版本号仍是 1.8.3，从未发布，真实 GUI 等人工验收项见 [接手回执](HANDOFF-windows-1.7.17-response.md)。这次发布会把它们一起发出去。
- 用户已授权：Windows 同步后发布新版本。

## 需要同步的行为

### 1. 隔离检查未通过改为警告，不阻止启动（用户决定）

规格见 `docs/SPEC.md`「存储校验 → 未通过时的处理」。Mac 参考：`src/main.m` 的 `checkAccount:allowRisk:completion:`。

| 场景 | 行为 |
| --- | --- |
| 启动或打开账号 | 警告，按钮“取消”（默认）／“仍然继续” |
| 选择继续 | 本次启动不再因隔离检查中止，含启动前的所有复核 |
| 下次点击 | 重新检查、重新警告，不记住选择 |
| 选择取消 | 不启动，账号保留错误状态 |
| 删除账号、登记项目 | 仍然阻止 |
| 路径已存在但不是目录 | 仍然阻止（确实无法启动） |
| 网络环境检测 | 不变 |

Windows 要改的位置（行号以当前 `dev` 为准，先核对）：

- `windows/src/LauncherUI.cs` `CheckAndOpen`（约 1654 行）：`ValidateStorage` 失败时直接 `Alert` 后返回，改为询问。
- 同文件约 1678 行：网络确认后再次 `ValidateStorage`，异常进入“启动前检查失败”。用户已接受风险时不能再因此中止。
- `windows/src/ClaudeLauncher.cs` `Core.Launch`（约 179、181 行）：两次 `Storage.Validate(..., false)` 会抛异常中止。需要传入“已接受风险”。`DeletionJournal.IsPending` 的检查保留。
- 删除流程的存储校验保持阻止。
- 新增文案加入 `resources/i18n/english.strings`，这两条 Mac 已有，不要改译文：`%@\n\n继续可能让账号之间共用数据，登录状态或配置可能互相影响。启动器不会修改这些文件。` 和 `仍然继续`。Windows 占位符用 `{0}` 时单独加键，不要覆盖 Mac 的键。

### 2. 同步链接的目标判定对齐

Windows 已有 `ConfigSync.SharedLinkAllowed`（`windows/src/ConfigSync.cs`），放行同步白名单位置的链接。Mac 1.7.18 额外判为未通过的目标，Windows 需要补齐：

- `%USERPROFILE%\.claude.json`；
- `%USERPROFILE%\.claude` 内白名单之外的内容（如 `.credentials.json`、`projects`）；
- 账号数据目录、`%USERPROFILE%\.claude`、启动器数据目录的上级目录（例如链接指向用户主目录）；
- 点开头的条目名（`.hidden.md`）。

Mac 用例见 `src/main.m` 的 `allowed`、`rejected` 两组测试，Windows 补上对应自检。Mac 只看链接目标元数据，Windows 的 `SafeTree` 还查内容树，这个差异保留，它抛出的异常同样走警告流程。

### 3. 用量趋势位置

Mac 把趋势从实例页移到账号管理页表格下方（建议条、每账号卡片、近 7 天图），规格和做法见 `docs/STATUS.md`「用量趋势移到账号页」。Windows 按相同结构调整；数据来源和保存规则不变。

---

## 提示词

```
你在 ClaudeLauncher 仓库的 Windows 端（windows/，C# 5 / .NET Framework 4.8 / WPF）工作。macOS 已发布 1.7.18，Windows 已发布版本是 1.8.3。任务：同步 Mac 这次的行为改动，验证后发布 Windows 新版本 1.8.4。

开始前必须完整阅读：
- AGENTS.md（最高优先级约束，两端都适用）
- docs/HANDOFF-windows-1.7.18.md（本次改动清单）
- docs/SPEC.md「存储校验」，以及「启动与激活」中的网络环境检测
- docs/STATUS.md 顶部三节、docs/HANDOFF-windows-1.7.17-response.md（上一轮未验证项）
- docs/RELEASE-macos-1.7.18.md
- windows/README.md、windows/docs/DEVELOPMENT.md
Mac 参考实现在 src/main.m（checkAccount:allowRisk:completion:、SharedConfigLinkAllowed 及其自检）、src/ConfigSync.m（ConfigSyncEntryAllowed）、src/UsageTrend.m，只作行为参考，不要照搬平台 API。

硬性约束（摘要，以 AGENTS.md 原文为准）：
- 管理功能只做本地操作，绝不向 Claude/Anthropic 及其云端、API、遥测、登录服务发请求，不启动 CLI/浏览器/WebView 间接查询，不读取登录凭据。
- 唯一允许的非 Claude 网络是本项目 GitHub Releases 的启动器更新：默认关闭，开启后每次打开启动器检查一次，并支持手动检查。
- 不修改官方 Claude，不强杀 Claude，不迁移或覆盖固定账号目录。
- 启动器不修复、不改写隔离检查发现的文件，只警告。

第 0 步：git status / git log 盘点本机未推送的提交和未提交改动。git fetch 后把 origin/dev 合入工作分支，不 rebase 已发布提交，不强推，不要用旧的 windows/ 目录覆盖。运行 Windows 自检，确认起点全部通过。

第 1 步：隔离检查未通过改为警告。启动或打开账号时弹窗列出原因和风险，按钮“取消”（默认，回车取消）和“仍然继续”。继续后本次启动不再因隔离检查中止，包括 LauncherUI.CheckAndOpen 网络确认后的复核和 Core.Launch 里的两次 Storage.Validate。下次点击重新检查、重新警告，不记住选择。删除账号、登记项目到账号仍然阻止；路径已存在但不是目录仍然阻止；网络环境检测不要改。

第 2 步：补齐 ConfigSync.SharedLinkAllowed 的目标判定（.claude.json、.claude 内白名单之外的内容、受保护目录的上级、点开头的条目名），并补对应自检。

第 3 步：用量趋势移到账号管理页表格下方，实例页去掉入口。数据来源和保存规则不变，不新增读取或网络。

第 4 步：同步文档。把 docs/SPEC.md 里这几处 [Windows 待同步] 标记改成已实现或写明差异，更新 windows/README.md、windows/docs/USER_GUIDE.md、docs/STATUS.md。新增 docs/RELEASE-windows-1.8.4.md。

第 5 步：验证。自检、离屏预览、安装替身和真实 GUI 分开报告，没做的标为未验证，不要把测试通过写成 GUI 通过。需要合成隔离检查失败时用临时目录或假主目录，不要在真实账号目录里制造链接。只启动少量实例，不碰真实登录数据。
重点检查：警告弹窗的取消和继续两条路径；继续后真的能启动；删除账号时仍被阻止；同步链接不触发警告。

第 6 步：发布。用户已授权这次发布 Windows 新版本。但下面任一情况先停下来告诉用户，等确认：自检没有全部通过；第 5 步的重点检查无法在真实 GUI 上完成；上一轮人工验收项（docs/HANDOFF-windows-1.7.17-response.md）里发现了会影响使用的问题。
确认后：版本号改为 1.8.4（windows/src/Updates.cs），提交，合并到 dev 和 main 并推送，用 windows/scripts/publish-release.ps1 发布。发布必须把当前已发布的 macOS 1.7.18 清单和 DMG 原样带入，不要重签，不能让 Mac 更新失效。发布后核对 latest/download 上的两端清单和文件哈希。

完成后汇报：分支和提交号、测试结果（自检与 GUI 分开）、未验证项、发布链接。
```
