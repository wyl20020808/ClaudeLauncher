# 交接：Windows 同步 macOS 1.7.15～1.7.17 功能

> 用法：在 Windows 开发机上 `git pull`（dev 与 main）后，把下面「提示词」整段交给那边的 Claude Code 或 Codex。

## 现状（2026-10-30，Mac 侧整理）

- macOS 已发布到 `macos-v1.7.17`。Windows 停在 `windows-v1.8.3`（目标 `ccb8cd2`）。
- 三个提交里的 Windows 改动写好了但从未在 Windows 上编译，合并 1.7.16 时 `windows/` 回退到了已验证状态，需要移植：`e2f19f6`（设置即时保存、Claude 退出后保留用量上次记录）、`425a16e`（启动前本机网络环境检测，新增 ProxyCheck.cs）、`8a569f7`（回环地址按实际配置连接）。
- Windows 目前没有项目管理、本地交接、账号配置同步、用量提醒和趋势、主题、英文界面、快捷键。

---

## 提示词

```
你在 ClaudeLauncher 仓库的 Windows 端（windows/，C# 5 / .NET Framework 4.8 / WPF）工作。macOS 已发布到 1.7.17，Windows 停在 1.8.3。任务：按优先级把 Windows 对齐到当前规格。

开始前必须完整阅读：
- AGENTS.md（最高优先级约束，两端都适用）
- docs/SPEC.md（两端行为以它为准；带 [Windows] 标记的条目是 Windows 专属差异）
- docs/STATUS.md 中 Windows 1.8.3 之后的全部记录，以及 docs/RELEASE-macos-1.7.15.md～1.7.17.md
- build/review-20261030/REVIEW.txt（Astra 审查报告，第 5 条是 Windows 问题）
- windows/README.md、windows/docs/DEVELOPMENT.md
Mac 参考实现在 src/（main.m、Settings.m、Projects.m、ProjectImport.m、Handoff.m、ConfigSync.m、UsageTrend.m、UsageInterface.m、ProxyCheck.m、Localization.m），只作行为参考，不要照搬平台 API。

硬性约束（摘要，以 AGENTS.md 原文为准）：
- 管理功能只做本地操作，绝不向 Claude/Anthropic 及其云端、API、遥测、登录服务发请求，不启动 CLI/浏览器/WebView 间接查询，不读取登录凭据。
- 唯一允许的非 Claude 网络是本项目 GitHub Releases 的启动器更新：默认关闭，开启后每次打开启动器检查一次，并支持手动检查。
- 日常运行不依赖 Python/Node。保持 C# 5 语法与 .NET 4.8。
- 在 dev 分支开发，验证后合并 main。改行为先改 SPEC，再改代码，最后更新 STATUS、使用说明、windows/README。
- 自检与 GUI 实测分开报告，未做的标「未验证」。不触碰真实登录数据，只启动少量实例。
- 推送、合并 main、发布前先让我确认。

第 0 步：git status / git log 盘点本机未推送的提交和未提交改动；git fetch 后把 origin/main 合入工作分支（不 rebase 已发布提交，不强推）；windows 自检全部通过后再开始下面的任务。

按以下顺序做，每一项一个功能分支（或一组独立提交），完成一项、自检通过、更新文档后再做下一项：

1. 移植三个未编译的 Windows 改动（最优先）
   - 用 git show e2f19f6 425a16e 8a569f7 -- windows/ 查看原改动，在当前 windows/ 上重新应用并编译：设置即时保存（去掉保存/撤销，切换即生效，失败恢复原值并提示；恢复默认立即保存）、Claude 退出后卡片保留用量上次记录（标“Claude 未运行 · 上次记录”）、启动新实例前的本机网络检测（ProxyCheck.cs，只连本机回环地址，回环地址按实际配置解析，覆盖 127.0.0.2/[::1]/localhost/非回环）。
   - LauncherTests 对应用例补齐并通过。

2. 可靠性修复（对照 macOS 已修复的同类问题）
   - Astra 第 5 条：CheckAndOpen 在 Storage.Validate 完成后立即 checking.Remove，之后才 await ScanNow。改为把每账号的操作占用保持到扫描、复核、启动/激活交接结束；ScanNow 结果按代次生效，旧扫描不能覆盖新状态。用可控制完成顺序的替身写测试，不用真实账号制造并发。
   - 更新任务隔离：取消更新后立即重试时，旧任务的下载/校验/安装回调不得影响新任务（参考 macOS 的 updateGeneration）。
   - 安装后重新打开失败：检查启动结果，失败时给出明确结果和恢复入口，不能静默当作成功。
   - settings/logs/diagnostics 写入前检查目录链上的重解析点（符号链接/联接点），文件须为普通文件、属于当前用户、有大小上限；写入失败要提示，清空日志先写盘成功再清内存。
   - windows/scripts/publish-release.ps1：发布后复核把“下载失败”和“内容不符”分开，下载失败只提示“待复核”并保留发布，只有确认内容不符才回滚。用本地替身测试超时、正确、错误三种情况。

3. 账号页与提醒（macOS 1.7.16）
   - 账号页行内创建/启动/打开、Code 用户名（只读 .claude.json 的 oauthAccount.displayName，不读其他字段，不持久化、不记录、不发送）、排序、最近使用、账号详情。
   - 用量提醒：五小时用量达阈值（默认 80%）提醒一次并推荐观察到用量最低的账号；按账号持久保存提醒时的重置说明去重，观察到回落才重新启用；日志只写账号编号。
   - 用量趋势（macOS 1.7.17，SPEC“用量趋势与轮换建议”）：只记录启动器已观察到的数值，8 天、4000 点、15 分钟去重，近 7 天图、按重置分段的用完时间估算、下一个账号建议；关闭用量显示时一并清除。

4. 项目管理与本地交接（Windows 目前完全没有）
   - 先实现公共项目清单（SPEC“项目清单”“公共清单存储与路径安全”）、添加目录、打开（复制路径并在资源管理器中显示）、识别并导入（只读各账号记录前缀，先确认 Windows 客户端的本地记录位置与格式，写进调查报告再实现）。
   - 本地交接（SPEC 与 src/Handoff.m）：写入交接规则到 CLAUDE.md（只追加、带标记、确认后写入、拒绝链接和硬链接）、复制“写交接文档”指令、交接给其他账号；启动器不代发消息。
   - “同步到账号”（写入 Claude 侧栏空归档记录）只在 macOS 的 Claude 4.29.0/4.4212.0 上验证过。Windows 端先只做只读调查（客户端版本、claude-code-sessions 位置与格式、空记录能否生成侧栏分组），写入 docs/ 调查报告并停下等我授权，不要直接写真实账号。

5. 账号配置同步（macOS 1.7.17，SPEC“账号配置同步”）
   - 白名单同 macOS：settings.json、顶层 *.md、skills/agents/commands/output-styles 下的条目；绝不碰 .claude.json、凭据、会话、历史、projects、plugins；settings.json 含密钥类字段时拒绝。
   - 默认只补缺，覆盖时旧内容进回收站；先复制到临时名再不覆盖地移动到位。
   - 链接模式：Windows 创建符号链接需要开发者模式或管理员权限。先检测能否创建；不能时自动退回拷贝模式并在界面说明，不要求提权，不修改系统设置。目录可评估使用联接点（junction），文件不要用硬链接。
   - 用临时目录写与 macOS TestConfigSync 对应的测试。

6. 外观与操作（macOS 1.7.15～1.7.17）
   - 主题跟随系统/浅色/深色；英文界面（复用 resources/i18n/english.strings 的同一份词条，缺失回退中文，格式占位符须一致）。
   - 登录时启动改为开关（HKCU Run 项或启动文件夹，注册失败要提示；开发目录副本拒绝注册）。
   - 快捷键：启动器在前台时 Ctrl+1～Ctrl+0 打开已有账号；可选全局 Ctrl+Alt+1～0（默认关闭，注册冲突要提示）。

完成标准：
- 每项都有对应测试并通过；Windows 构建、LauncherTests 全部通过。
- 每项在 docs/STATUS.md 记录做了什么、怎么验证的、还有哪些没验证；SPEC 中 Windows 差异用 [Windows] 标注。
- 做完一批后汇报：分支/提交号、测试结果、未完成或有疑问的地方。发布 Windows 新版本前先让我确认。
```
