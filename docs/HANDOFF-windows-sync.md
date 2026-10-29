# 交接：Windows 同步 macOS 1.7.10 功能

> 2026-10-29：后续增量见 [HANDOFF-windows-1.7.14.md](HANDOFF-windows-1.7.14.md)。本文保留原 1.7.10 内容。

> 用法：在 Windows 开发机上 `git pull` 后，把下面「提示词」整段交给那边的 Claude Code。

## 现状（2026-10-28，Mac 侧整理）

- macOS 已发布到 `macos-v1.7.10`（main `11adb8f`）。Windows 停在 `windows-v1.7.3`，自 `7dfb8cb` 之后 `windows/` 目录没有任何改动。
- Windows 1.7.3 已有：实例管理、账号管理、日志、系统设置（只有日志目录／数据目录／检查更新可用，其余是占位）、托盘图标、登录保护、删除账号到回收站、更新进度窗口、自建 + GitHub 取较高版本、启动 8 秒后 24 小时自动检查。
- Windows 缺失或与当前规则冲突的部分见提示词中的任务清单。

---

## 提示词

```
你在 ClaudeLauncher 仓库的 Windows 端（windows/，C# 5 / .NET Framework 4.8 / WPF）工作。macOS 已发布到 1.7.10，Windows 还停在 1.7.3。任务：把 Windows 同步到当前规格。

开始前必须完整阅读：
- AGENTS.md（最高优先级约束，两端都适用）
- docs/SPEC.md（两端行为以它为准；顶部「本地安全与可操作界面」一节优先于下方旧更新描述）
- docs/STATUS.md 中 1.7.3 之后的记录（Mac 1.7.4～1.7.10）
- docs/SIDEBAR-LOCAL-RESEARCH.md、docs/USAGE-RESEARCH.md（做第 5、6 项前读）
- windows/README.md、windows/docs/DEVELOPMENT.md
Mac 参考实现在 src/（main.m、Settings.m、Projects.m、ProjectImport.m、UsageInterface.m），只作行为参考，不要照搬平台 API。

硬性约束（摘要，以 AGENTS.md 原文为准）：
- 管理功能只做本地操作，绝不向 Claude/Anthropic 及其云端、API、遥测、登录服务发请求，不启动 CLI/浏览器/WebView 间接查询，不读取登录凭据。
- 唯一允许的非 Claude 网络是启动器更新：默认关闭、用户在设置中开启并保存后才生效；开启后只在每次打开启动器时检查一次 + 手动检查；禁止定时/重复后台检查；来源只用本项目 GitHub Releases。
- 日常运行不依赖 Python/Node。保持 C# 5 语法与 .NET 4.8。
- 在 dev 分支开发，验证后合并 main。改行为先改 SPEC，再改代码，最后更新 STATUS、使用说明、windows/README。
- 自检与 GUI 实测分开报告，未做的标「未验证」。不触碰真实登录数据，只启动少量实例。

按以下顺序做，每一项一个功能分支，完成一项、自检通过、更新文档后再做下一项：

1. 更新网络边界（合规，最优先）
   - 客户端移除自建服务来源（Updates.cs 的 BaseUrl 与「取较高版本」逻辑），只取
     https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-windows.json 与同 Release 的 Setup。
   - 跳转只允许本项目 Release 下载路径及 objects.githubusercontent.com / release-assets.githubusercontent.com；只 HTTPS、443、无用户信息、无 Cookie/凭据/缓存；其他域名（含 Claude/Anthropic）一律拒绝。URL 校验集中在一处。
   - 新增设置项 allowUpdates，默认关闭；删除「启动 8 秒后、距上次超过 24 小时」的自动检查与 last-check 文件逻辑；开启后每次打开启动器检查一次（只在有新版时提示，失败只记日志）+ 手动检查。
   - 有未保存设置时不开始更新；下载期间出现未保存更改时，安装前取消并清理临时文件。
   - 自检加离线拒绝测试：未开启时不发请求、非白名单 URL/跳转在建立连接前被拒。
   - 发布脚本 publish-release.ps1 与自建服务 sync.py 暂保持原样：1.7.3 及更早客户端仍会读自建服务，过渡期继续发布到自建服务。只改客户端。

2. 系统设置六分类接通（对照 SPEC「系统设置」表格，做 Windows 等价物）
   - 草稿 → 保存 → 撤销 → 恢复默认；跨分类保留草稿；退出前提示未保存；值按白名单校验，异常回落默认。保存到启动器自己的设置文件（%LOCALAPPDATA%\Claude-MultiLauncher 下），不写 Claude 数据。
   - 常规：关闭窗口继续运行（默认开）、托盘图标（默认开）。
   - 启动行为：开机启动用 HKCU\...\Run 管理本启动器（单独确认、立即生效、仅接受正式安装路径）；恢复上次页面（默认关）、默认页面、本地进程刷新间隔 1/3/5 秒、点击账号时恢复最小化窗口（默认开）。绝不自动启动 Claude。
   - 外观：显示水墨插画；「显示本地界面用量」在第 6 项实现前先不放或置灰说明。
   - 数据与存储：显示/复制数据目录、导出偏好 version 1 JSON（不含账号、别名、项目、凭据）、内存日志上限 100/500/1000（默认 500）。
   - 消息通知：窗口内提示（默认开）、系统提示音（默认关）、测试按钮。
   - 关于：版本、离线帮助、复制基础诊断（仅版本/系统版本/联网开关）、更新开关与检查按钮。
   - 删除虚假或越界选项（默认浏览器、自动恢复 Claude 等）。

3. 账号页与日志页交互（SPEC「账号与日志交互」）
   - 账号：按别名/编号/目录搜索，状态筛选（运行中/未运行/处理中/异常），铅笔按钮设 ≤24 字本地别名（拒绝控制字符，留空恢复默认，实例卡片同步），复选框全选/部分选中，「复制选中路径」只复制可见选择，筛选隐藏时清除选择。没有批量删除/批量启动。
   - 日志：本次运行/5 分钟/1 小时、实例、级别、全文搜索组合筛选；每页 10 条分页；导出全部筛选结果；清空需确认且只清内存。日志不持久化。
   - 已有的删除账号（回收站、01 不可删、运行中不可删、删除前重扫）保持，确认与 AGENTS 新规则一致即可。

4. 项目管理（SPEC「项目清单」「公共清单存储与路径安全」）
   - 左侧导航顺序：实例管理、账号管理、项目管理、日志记录、系统设置。
   - 清单 %LOCALAPPDATA%\Claude-MultiLauncher\projects.json（格式同 SPEC，原子写入、仅本人可访问、损坏改名保留、未知版本不覆盖）。
   - 添加（文件夹选择器）、改名称/分组、上下移、在资源管理器显示、复制路径、移除（只删记录）。路径准入：绝对真实路径，拒绝账号数据目录、ClaudeCodeConfig、启动器数据目录及安装目录；重解析点按链接处理；主目录/盘符根目录需额外确认。
   - 「用账号打开」复用现有选择账号的完整流程（PID 核对、登录保护、存储检查、防重入），通过后复制路径并在资源管理器显示，提示用户在 Claude 中选择该文件夹。不用 claude:// 协议。
   - 「识别并导入」：先只读确认 Windows 版 Claude 中 claude-code-sessions/<账号UUID>/<组织UUID>/local_<UUID>.json 的实际位置和字段顺序（含 MSIX 虚拟化路径），确认与 Mac 4.29 格式一致后，再按 SPEC 的前缀读取规则（只读 sessionId/cliSessionId/cwd/originCwd，读完即停，上限 20000 目录项/5000 记录/32768 字节）实现。格式不同则停下来报告，不要猜。

5. 登记到 Claude 侧栏（SPEC「登记到 Claude 侧栏」）——可选，需先验证
   - Mac 方案只在 Claude 4.29.0 上验证过。Windows 先只读调查当前 Claude 版本的侧栏与空归档记录行为，报告结果并等用户授权后才写入任何 Claude 目录。未经实测不宣称完成。

6. 本地界面用量——只做调查，不直接实现
   - Mac 用辅助功能（AX）只读官方 Code 页面的用量控件，并仅为缓存归属读取 lastActiveOrg 指纹（该读取是用户在 Mac 上的单独授权，不自动延伸到 Windows）。
   - Windows 若用 UI Automation 实现，以及是否涉及读取 Cookies/DPAPI，都必须先写调查报告并取得用户明确授权。授权前不读取 Cookies、Local State 或任何加密数据。

版本与发布：
- 每完成可发布的一组功能递增 Windows 版本（建议第 1～3 项完成后发 1.8.0），改 Updates.cs AssemblyVersion/Version/DisplayVersion 与 installer/ClaudeLauncher.iss。
- 用 windows/scripts/publish-release.ps1 只发布 Windows（标签 windows-v<版本>），从当前 Latest（macos-v1.7.10）原样带入 macOS 1.7.10 的 DMG 与 latest-macos.json（验签 + 核对哈希，不重签）。发布前先问我确认。
- 发布后在 STATUS 记录：标签、目标提交、Setup 大小与 SHA-256、两端清单复核结果、未验证项。

每轮结束汇报：改了什么、自检结果（数量）、GUI 实测了什么、哪些未验证、下一步。
```
