# 项目状态

## Windows 1.7.3 发布（2026-10-29，windows-v1.7.3）

- 内容：下节“取较高版本”。版本号改为 1.7.3（`ebac307`），`build.ps1` 通过（LauncherTests 246 项、自检 140 项）。
- 首次运行 `publish-release.ps1` 在带入 Mac 文件时停止（未上传、未改动）：Mac 1.7.6 清单由 Mac 密钥签名，脚本只认共用公钥。修正为 Mac 清单接受共用公钥或 `resources/update-public-key-macos.xml`，发布后复核同样适用（`5beb06a`）。
- 只发布 Windows：标签 windows-v1.7.3（Latest，目标 `5beb06a`），含 `ClaudeLauncherSetup-1.7.3.exe`（2794723 字节）、`latest-windows.json`，以及从 macos-v1.7.6 原样带入并验签核对的 `ClaudeLauncher-1.7.6.dmg` 与 `latest-macos.json`。自建与 GitHub 复核：windows 1.7.3、macos 1.7.6 全部通过。
- 未验证：客户端 1.7.2 → 1.7.3 的实际更新（1.7.2 仍按旧逻辑先查自建，自建已是 1.7.3）；1.7.3 起的进度窗口来源显示与取较高版本的 GUI 表现。

## Windows 取较高版本（2026-10-29，feature/windows-higher-source，已随 1.7.3 发布）

- 对齐 SPEC“来源与回退·取较高版本”：`Updates.CheckAll` 并发向自建与 GitHub 取 `latest-windows.json` 并各自验签，失败原因逐来源记录；`Updates.Candidates` 在通过的清单中取最高版本，同版本按自建优先排列。界面先比较版本再询问，下载失败或大小/SHA-256 不符换同版本下一个来源；只有全部来源无有效清单才算失败，列出各来源原因并提供“前往 GitHub 下载”。进度窗口新增“来源：…”一行。版本号未改（仍 1.7.2），未发布。
- 自检（`--self-test` 140 项通过，新增 6 项）：自建落后选 GitHub；同版本自建优先、GitHub 备用；自建失败用 GitHub；全部失败为空；1.10.0 > 1.9.9；来源失败记录原因不抛出。`build.ps1`：LauncherTests 246 项、自检、离屏预览通过（下载状态截图含“来源：GitHub”）。
- 实测（构建产物代码，非 GUI）：并发取两个真实来源均为 1.7.2，候选顺序“自建服务、GitHub”。
- 未验证：桌面 GUI 中的实际更新流程（需发布新 Windows 版本后，从 1.7.2 以上版本触发）；自建落后场景的端到端更新。

## macOS 1.7.6 发布（2026-10-28）

- `publish-macos.sh` 发布 macos-v1.7.6（Latest，目标 b06530d）：DMG 1768902 字节，sha256 bd0d6424617a864db46a1d9d21313fda4748335ff76eb74e2ee053b69c1efa65，Mac 私钥签名；Windows 1.7.2 文件原样带入并核对。
- 自建落后真实场景（10:56:09，自建 1.7.5、GitHub 1.7.6）：1.7.5 测试副本（新逻辑）同时取两个来源，选 GitHub 的 1.7.6 并完成替换。此前未验证项通过。
- 自建服务 10:59:15 同步到 1.7.6（发布后约 4 分钟）。
- 已安装的 1.7.5 仍是旧逻辑（先查自建），需等同步后才看到 1.7.6；从 1.7.6 起不再受同步延迟影响。

## 更新来源取较高版本（2026-10-28，feature/update-best-source，macOS 1.7.6 / build 13，未发布）

- 客户端同时向自建服务与 GitHub 取 `latest-macos.json`，各自验签后取最高版本；同版本按自建优先下载，下载失败或哈希不符换下一个来源；全部来源无有效清单才失败。解决 1.7.5 发布时自建同步前客户端误判“已是最新”。
- 自检新增：自建落后时选 GitHub、同版本保留自建优先与 GitHub 备用、数值比较与空输入；arm64、x86_64 共 9 组通过。
- 端到端（1.7.4 测试副本，`--update-test`）：两个来源均为 1.7.5 时从自建下载并完成替换；自建不可达时由 GitHub 完成替换；无临时目录残留。
- 未验证：自建清单比 GitHub 旧的真实场景端到端（需在自建服务同步前测试，留待下次发布时观察）；GUI 点击。Windows 待同步。

## macOS 1.7.5 发布（2026-10-28）

- 内容：本地用量历史（被动读取官方 plan-usage-history.json）、项目目录登记到 Claude 侧栏（仅 Claude 4.29.0）。发布前审查通过，arm64 与 x86_64 自检 8 组通过。
- 首个由 Mac 发布私钥签名、用 `scripts/publish-macos.sh` 自行发布的版本（macos-v1.7.5），Windows 文件从当前 Latest 原样带入。用户确认现在发布：停在 1.7.3 及以下的客户端收不到此版本，需手动下载。
- 发布：`publish-macos.sh` 全流程通过（构建、Mac 私钥签名与验签、带入 Windows 1.7.2 文件并核对、草稿转正式并设为 Latest、latest/download 复核）。DMG 1761386 字节，sha256 a2b16a6770c363eebd46ca361a86db9de915dfd19142d7dfe279d8c678d4df2e，目标提交 9345c27。
- GitHub `latest/download` 发布后约 1 分钟内仍返回 1.7.4（CDN 缓存），10:33 起为 1.7.5。
- 自建服务 10:39:43 同步到 1.7.5（发布约 7 分钟后），Mac 密钥签名首次经同步上线；自建清单用 Mac 公钥验签通过，DMG 哈希一致。期间一次 SSL 连接超时，重试成功。
- 同步前客户端先查自建服务，拿到合法的 1.7.4 清单即判定“已是最新”，不回退 GitHub（符合当前规则）。待办建议：客户端同时取两个来源，取较高版本，避免自建落后时漏更新。
- 界面内更新 1.7.4 → 1.7.5（10:47，用户点击）：版本 1.7.5 / build 12，签名校验通过且证书不变，两把公钥均在包内，无备份或暂存残留，备份记录已清除，无临时目录残留，辅助功能 accessibility=yes。1.7.4 客户端接受 Mac 密钥签名的清单，首次真机验证通过。

## 项目目录登记与本地用量功能合并（2026-10-28）

- 按用户要求，将目录登记功能 `6c27ce1` 合入本地 dev，保留 dev `fdc46f2` 已有的本地用量显示；未修改 Windows。仅 `src/main.m` 自检入口发生文本冲突，保留 `TestProjectRegistration` 与 `TestUsageHistory` 两项调用。
- 固定证书构建 `SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh build/project-registration-merge` 成功，plist 与签名校验通过；原生和 x86_64 自检各八组 PASS，`git diff --check` 通过。
- GUI：本轮合并后未重新执行，标为未验证；合并前目录登记的真实 01 → 02 验收见下文。本轮未启动／退出 Claude，也未向真实账号写入记录。
- 仅本地合并与提交；未推送、发布、替换日常安装版。版本沿用 1.7.4 / build 11。下方各分支阶段报告保留当时结论。

## 原生侧栏目录登记已接入（2026-10-28，feature/project-registration）

用户明确授权保留空归档记录，并将 01 的真实项目目录登记到 02。本分支合并 origin/dev 后继续实现，保留既有手动添加、识别全部已创建账号（含未运行账号和历史记录）、公共清单及账号隔离流程。版本沿用 dev 的 1.7.4 / build 11；未新增版本号、打包、安装、合并回 dev 或推送。Windows 未修改。

### 实现

- 项目页新增“登记到账号…”：勾选公共清单中的目录并选择一个正常退出的账号，只为缺少的目录新建独立 UUID 的七字段空归档。
- `src/ProjectImport.m/.h`：只读前缀去重、唯一目标身份目录检查、目录锁、0600 独占临时写入和不覆盖原子发布；路径及目标进程在发布前复核。部分成功明确报告，重试跳过已有目录。
- `src/main.m`：目标版本限定 Claude 4.29.0，复用进程／登录保护／存储检查，增加 GUI 和同后端开发诊断命令。账号启动、隔离及删除流程保持原样。
- 来源只提供真实目录路径；空记录的 ID、标题、时间均重新生成，没有来源会话正文、旧 ID、Cookie、登录、MCP、权限或环境字段。不复制来源完整记录再删字段，不读取项目文件内容。

### 构建和合成自检

- `SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh build/project-registration`：成功，plist 与 codesign 严格校验通过，固定证书指纹 `5ce1d074d09ff41a6347a3bbcaff92c8fe25bda5`。未修改系统权限或签名身份。
- 原生与 `arch -x86_64 ... --self-test`：各七组 PASS。新增覆盖七字段／独立 UUID／0600、前缀兼容、无写入预览、批量往返与去重、部分失败重试、运行中拒绝、目录变化、多组织、未知前缀、链接与并行写入拒绝；随机合成目录均清理。
- 第 2 页离屏截图 `build/project-registration/projects-demo.png`：已查看，新增按钮与表格布局正常。离屏截图不替代下列真实 GUI 验收。

### 真实文件检查与 GUI 验收（Claude 4.29.0）

- 02 运行时预览被拒绝，未写入。正常退出后，01 识别到三个有效目录；预览 planned=3，正式执行 added=3，重复执行 added=0 / skipped=3。
- 在再次启动 Claude 前，仅检查本次新增文件：三个新 UUID、精确七字段、isArchived=true、0600、单硬链接；目标原有三个目录项的 inode／大小／mtime／ctime 等元数据均未改变。未读取原有文件正文或凭据。
- GUI 实际打开开发版项目页、登记弹窗、选择 02 和三个目录，完成重复登记；结果为新增 0、跳过 3。随后使用启动器既有流程启动 02，主进程诊断确认只运行 02。
- 开启官方“分组和排序 → 显示 → 空分组”后，02 左侧实际出现 `ClaudeLauncher`、`ConjoinedTwins`、`talk-like-human`，均为“无活跃会话”；原有“对话测试”仍显示。
- `ClaudeLauncher` 的“新建会话”打开空白草稿，路径为原目录。另两个入口分别进入对应路径的官方信任提示，均取消，没有授予新信任。未输入或发送消息，未连接 GitHub、未创建云端任务。
- 正常退出并再次用启动器启动 02 后，三个分组仍显示。最终保留这些真实目录的空归档及空分组显示设置，02 停在 `ClaudeLauncher` 空白草稿，01 保持未运行。测试使用开发构建；/Applications 中的日常 1.7.3 未替换。

### 未验证与使用边界

- 未验证：另外两个目录确认信任后的实际发送、长时间保留／官方自动清理、其他 Claude 版本、更多账号／组织组合、大量项目的真实 GUI 滚动。拒绝和竞争分支有合成自检覆盖，不等同于全部真实场景验收。
- 此登记代码只操作本机，不调用官方项目／会话 API；Claude 启动和后续正常使用仍有其自身联网行为，本轮未做网络抓包，也不据此承诺零封号风险。
- 空归档需保留，可能在归档列表可见；第一版不提供批量撤销。操作时目标必须退出，且不要从 Dock／终端另行启动它。启动器不能阻止外部启动与最后检查之间的极短竞争窗口。
- 当前实现与验收没有待用户决定的阻塞项；发布、安装及后续扩展另行进行。

## 原生侧栏本地项目调查（2026-10-28，feature/sidebar-local-research）

后续用户追加授权后，完成了“全新空归档记录”的单账号实验：开启空分组后，02 原生侧栏出现测试目录，显示“无活跃会话”；点击新建准确进入该目录的信任确认（已取消，未发送消息）。正常重启后保留；删除唯一实验记录并重启后消失。实验记录、目录已清理，显示设置及原目录选择已恢复。新增开发专用 `scripts/check-sidebar-seed.cjs`，字段白名单／独立 UUID／0600 往返及实际纯分组函数验证通过。批量产品功能未实现，真实发送、长期保留和未来版本兼容未验证。下列第一阶段记录保留历史结论，完整结果见调查报告第二阶段。

- 用户目标已明确为所有账号的 Claude 原生侧栏显示目录，现有公共清单导入不满足此目标；SPEC 已记录新的验收条件。
- Claude 4.29.0 GUI 实测：合成空目录保存为本地环境成功、落盘成功；按文件夹分组、显示空分组、按环境分组均没有生成该目录的侧栏入口。目标效果未通过，不作为已完成功能发布。
- 已定位 `ccd-session-presets.json`、渲染进程缓存和从会话生成分组的代码路径；未找到符合当前约束的可靠配置入口。测试环境及随机空目录已清理，显示设置已恢复。
- 本轮没有产品源码改动，未构建、未执行自检、未打包发布。批量配置和重启持久化未验证。详细证据和边界见 [本地侧栏调查](SIDEBAR-LOCAL-RESEARCH.md)。

整理日期：2026-10-28。源码与本机日常 build/：1.6 / build 7（未发布）；历史交付：1.5 / build 6。

## 本地用量读取合并到 dev（2026-10-28）

- 按用户要求，把 `codex/usage-limits-research` 的功能提交 `237ceef` 快进合并到本地 `dev`；从 `432d2d5` 前进，无冲突。原侧栏研究分支保持不变。
- 源码和构建脚本与上一轮已验证版本完全一致；沿用上一轮 arm64／x86_64 七组自检与 GUI 结果，本轮不重复构建或宣称新增 GUI 验证。合并后检查提交归属、源码一致性与工作区状态。
- 逻辑保持被动读取：启动器切前台时读取官方本地用量历史，不新增查询、不读取凭据、不触发官方刷新。同步 README、SPEC 与使用说明的合并状态，下方实验记录保留原结论。
- 同步合入远端 `14694e0` 的 1.7.4 发布记录；仅 STATUS 同位置插入冲突，双方记录完整保留，源码没有冲突。
- 版本号仍为 1.7.4 / build 11。本轮未推送、未合并 main、未发布、未替换 /Applications 中的日常安装版。

## 本地用量历史实验（2026-10-28，codex/usage-limits-research）

- 从本地 dev `432d2d5` 创建独立工作区 `/Users/apple/.codex/worktrees/usage-limits/ClaudeLauncher` 和分支，未影响原侧栏研究分支及未跟踪脚本。版本沿用 dev 的 1.7.4 / build 11，仅为开发构建，未打包、发布、安装、合并或推送。
- 按用户最终要求，采用被动读取官方已保存数据：切前台／回到实例页时后台读取各固定目录内 `plan-usage-history.json`，显示五小时／周已用百分比及“本地记录”时间。不发限额请求、不调用官方 IPC、不注入客户端、不读凭据、不改变官方轮询频率。详见 [调查与成本／风险结论](USAGE-RESEARCH.md)。
- 多组织／未知组织、15 分钟以上的旧记录、未来时间、缺失、未知格式或不安全路径不显示百分比；单组织记录也不能证明当前登录身份。没有重置时间数据，不编造。原有每秒进程刷新只更新过期显示，不读取用量文件。Windows 暂未接入。
- **构建／自检**：固定证书构建到 `build/usage-check/`；arm64、x86_64 七组自检通过，最终编译无警告。新增合成测试覆盖格式、零与缺失、非法百分比、过期／未来、多个组织、链接／硬链接／FIFO／权限／大小上限。随机合成目录已清理，`git diff --check` 通过。
- **本机只读诊断**：账号 01 多组织，显示“身份待核对”，1.931 毫秒；账号 02 有可读历史，0.310 毫秒。诊断只输出状态与耗时，不输出组织标识或百分比。未创建的账号返回“暂无本地记录”。
- **离屏渲染**：`build/usage-check/usage.png` 已查看，合成百分比、进度条、来源时间和身份待核对布局正常。
- **GUI**：官方 Code 用量弹窗点击到观察到结果约 919 毫秒，不能视为纯网络延迟。新启动器首次打开与实例页返回均显示真实本地记录／歧义状态，页面返回到观察结果约 501 毫秒（含自动化开销）。测试仅正常退出／重开启动器；结束后已恢复 `/Applications` 的 1.7.3。未退出、重启或启动 Claude，未提交聊天。
- **签名**：保持 `ClaudeLauncher Local Signing`，certificate leaf `5ce1d074d09ff41a6347a3bbcaff92c8fe25bda5`，未改变系统权限或官方应用签名。
- **限制／未验证**：后台静置和反复失焦的长期压力测试、真实登录身份与切换登录后的正确归属、其他 Claude 版本／所有订阅类型、Windows GUI 未验证。官方历史至少约 4.5 分钟才可能追加，并非实时数据；不保证切前台时能拿到新记录，不承诺账号零封禁风险。
## macOS 1.7.3 → 1.7.4 界面内更新实测（2026-10-28 10:20）

用户在 /Applications 的 1.7.3 中点击“检查更新”→“立即更新”，完成后核对：

- 版本 1.7.4 / build 11，`codesign --verify --strict` 通过，签名要求不变（certificate leaf 5ce1d074…），内置共用公钥与 `macos-publisher-key.xml` 均在，自建更新地址保留。
- 旧版备份已清理：/Applications 中无 `.Claude 启动器-backup-*` 或 `-update-*` 残留，`updateBackupPath` 偏好已清除（新版启动后把旧版移到废纸篓这一步此前未验证，本次通过；废纸篓内容受隐私保护未直接读取）。
- 本次更新未留下临时目录（替换脚本自清理生效）；发现的 05:35 遗留目录来自 1.7.0 的旧替换脚本（当时尚无清理），已删除。
- 辅助功能授权 accessibility=yes；新版从 /Applications 运行（10:20:13 启动）；Claude 01、02 进程启动于更新前，未受影响。
- 未验证：“日志记录”页中的“已更新到 1.7.4”记录（日志只在内存中，需用户目视）。

## macOS 1.7.4 发布（2026-10-28，macos-v1.7.4，Windows 发布机，最后一次由共用密钥签 Mac）

- 草稿目标 `d4cb8a6`；DMG 1686728 字节、SHA-256 与 Mac 端给出一致。Mac 公钥文件只含 Modulus/Exponent，RSA-3072，与共用公钥不同。
- 共用 DPAPI 密钥在内存中签名 `latest-macos.json`（原文逐字节核对）并验签；Windows 1.7.2 清单与 Setup 从当前 Latest（macos-v1.7.3）下载，验签并核对大小/哈希后原样上传。自建服务放入 DMG 并切换 macOS 清单（旧清单备份 `latest-macos.json.before-1.7.4`），Windows 文件未动。
- 转正式并设为 Latest；自建与 GitHub 两端清单和包复核：windows 1.7.2、macos 1.7.4 全部通过。之后手动运行一次 `sync.py`：两端均为最新，macOS 信任共用公钥与 `resources/update-public-key-macos.xml`；`tests/sync_test.py` 18 项通过。
- 此后 Mac 版本由 Mac 发布机用自己的密钥签名，自建服务自动同步。未验证：Mac 1.7.3 → 1.7.4 应用内更新；第一个 Mac 密钥签名版本经同步上线。

## macOS 独立签名（2026-10-28，feature/mac-signing-key，1.7.4 / build 11）

- 本机生成 Mac 发布私钥（RSA-3072，登录钥匙串，不可导出；用户选择不备份）。首次生成时“不可导出”参数位置错误，检查发现私钥实际可导出（仅在检查程序内存中，未输出或保存），该密钥从未用于签名，已删除并按正确参数重建，工具增加导出自检。公钥提交为 `resources/update-public-key-macos.xml`。
- 客户端接受共用公钥或 Mac 公钥任一验签；自检覆盖两把各自签名通过、未知密钥与空密钥拒绝、两把内置公钥均为 RSA-3072 且不同。自检发现 NSBundle 把 `*-macos.xml` 当作平台变体顶替共用公钥，包内改名为 `macos-publisher-key.xml`。arm64、x86_64 自检均通过。
- 新增 `scripts/release-key.m`、`scripts/publish-macos.sh`。
- 1.7.4 为过渡版，按 SPEC 由 Windows 发布机用共用密钥签名发布；1.7.5 起用 publish-macos.sh 自行发布。停留在 1.7.3 及以下的客户端不认 Mac 密钥，1.7.4 需先发布并保留一段时间。
- 未验证：GUI 点击更新到 1.7.4；publish-macos.sh 的正式发布路径（本轮只做 dry-run）。

## 两端独立发布：自建服务自动同步（2026-10-28，feature/self-hosted-sync）

- 用户同意按推荐方案解除 Mac 发布对 Windows 发布机的依赖：自建服务自动从 GitHub 同步；Mac 换独立签名密钥（过渡版由共用密钥签名）；Mac 发布脚本自行带入 Windows 文件。本分支只做 Windows 侧的同步；Mac 侧改动见交接，尚未实现。
- `windows/updates/sync.py`（仅标准库，纯 Python RSA PKCS#1 v1.5 验签），由 `server.py` 后台线程每 10 分钟运行。规则见 SPEC“来源与回退”和 `windows/updates/维护说明.md`。
- 检查：`python -I tests/sync_test.py` 18 项通过（测试密钥、file:// 来源：切换、重复、降级、错签、错误密钥、错平台、包不符、包过长、同名不同内容、`.previous`；正式公钥验证本机两份正式清单）。用 GitHub 实际数据在临时目录演练：旧 Mac 1.7.2 清单自动切到 1.7.3（DMG 下载核对），再次运行不重复下载，Windows 保持 1.7.2。
- Windows 客户端代码未改，未重新构建。

## macOS 1.7.3 发布（2026-10-28，macos-v1.7.3，Windows 发布机）

- 按 [RELEASE-macos-1.7.3.md](RELEASE-macos-1.7.3.md) 执行；Windows 保持 1.7.2，未改代码、未重新构建，只加载已有 1.7.2 构建中的验签代码。
- 草稿目标 `b6edb00`（含项目导入 `3a6b452` 与 1.7.3 构建 `b54a3e5`）。草稿 DMG 大小与 SHA-256 与交接一致；带入的 Windows 1.7.2 清单验签、Setup 大小与哈希一致。
- 用 DPAPI 发布密钥在内存中签名 `latest-macos.json`（签名原文与交接逐字节一致），用仓库公钥验签后上传；DMG 复制到自建 `public/`，原 macOS 清单备份为 `latest-macos.json.before-1.7.3`，原子切换。自建 Windows 清单、`latest.json` 和 Setup 未动。
- 转正式并设为 Latest。自建与 GitHub `releases/latest/download/` 分别取两端清单和安装包：windows 1.7.2、macos 1.7.3，验签、版本、大小、SHA-256 全部通过。
- 未验证：Mac 上 1.7.2 → 1.7.3 应用内更新、真实导入 GUI。

## macOS 1.7.3 发布准备（2026-10-28）

- 用户要求合并、推送并提供 Mac 应用内更新。本轮只更新 macOS 至 1.7.3 / build 10；Windows 保持 1.7.2，未改源码或重建安装包。合并 origin/dev 的 1.7.2 发布记录时，仅 STATUS 冲突，双方记录均保留。
- 构建：`SIGN_IDENTITY="ClaudeLauncher Local Signing" UPDATE_BASE_URL="https://www.u4139693.nyat.app:53569/claude-launcher/" /bin/zsh scripts/build.sh build/release-1.7.3`。签名、Info.plist 和 arm64／x86_64 六组自检通过；与 /Applications 已安装 1.7.2 同签名，未替换日常应用。
- `ClaudeLauncher-1.7.3.dmg`：1676413 字节，SHA-256 `55ecb85eb81d6deb2874413d49fad4a6cd210f6d4e11ad9e128450144a0682b0`。DMG 校验、只读挂载后的版本／更新地址／签名核对通过，1.7.3 项目页离屏截图已查看。
- 发布目标 `macos-v1.7.3`；原样带入的 Windows 1.7.2 清单和 Setup 已验签并核对大小及哈希。需要原 Windows 发布机签名 macOS 清单、切换自建服务并转正式；该机器无连接，本机无法代签，完成前保持草稿且旧 Latest 不变。交接见 [RELEASE-macos-1.7.3.md](RELEASE-macos-1.7.3.md)。
- 未验证：真实导入 GUI、完整导入弹窗、1.7.2 → 1.7.3 应用内自动更新。没有操作账号登录数据，没有退出或启动 Claude。

## macOS 项目识别与导入（2026-10-28，feature/project-import）

- 基于本地 dev `5cb6626` 新建分支。先更新 SPEC，保留已有手动添加／编辑／排序／移除／账号打开，新增“识别并导入”。本轮只改 src/、scripts/build.sh、README 和 docs；Windows、账号隔离和登录流程不变，版本保持 1.7.2 / build 9。不合并、不推送、不安装、不打包发布。
- 范围：全部已创建 01～10 账号，不依赖运行状态。只枚举固定三层的本地记录路径，支持仍留在本机的历史／归档记录。逐字节读取官方 4.29 格式的四个开头字段，路径结束后立即关闭；不读取后续正文、凭据、环境变量或项目内容，不复制／改写客户端数据，不请求官方接口。
- 可勾选的预览显示来源账号和逐账号统计，确认后复核路径并去重追加。保留已有名称、分组、顺序和上次账号；取消不写入，新项目上次账号为空。链接／权限／格式／扫描上限保守处理并报告。
- 限制：不是完整官方项目接口，无法覆盖已删除记录、未知／旧格式、额外工作目录或云端项目；本机同名远程路径需在预览核对。导入只更新启动器公共清单，不同步文件、会话或客户端侧边栏。根目录、主目录、临时目录走手动入口。
- 构建：`SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh build/project-import-check`，通用二进制；编译、Info.plist 和签名校验通过。证书 leaf 仍为 `5ce1d074d09ff41a6347a3bbcaff92c8fe25bda5`，不替换顶层 build/ 或 /Applications。
- **合成自检**：arm64、x86_64 六组通过。新增用例验证读取偏移恰好停在正文之前、未知字段的值不读取、多账号离线识别、原目录优先、路径转义、上限、链接／权限拒绝、跨账号去重、重复导入、取消不保存、路径失效复核、清单往返和移除不删文件。测试只使用随机假目录并清理。
- **本机只读兼容性检查**：`--project-import-check` 发现 2 个已创建账号、3 个去重候选；账号 01 有 18 份可用目录记录，账号 02 有 1 条受路径保护规则拒绝的记录；无未知格式、读取失败或上限截断。只输出计数，不输出路径和记录标识，不载入／写入真实 projects.json；这不是 GUI 导入成功证明。
- **离屏截图**：`build/project-import-check/projects.png` 已查看，五项导航、原表格与新增入口布局正常；`import.png` 的候选勾选、路径和账号统计区域已查看。NSAlert 部分原生标题／按钮未完整进入离屏缓存，完整弹窗外观仍**未验证**。
- **真实 GUI**：导入确认／取消、滚动长列表、已有清单保存、运行期间交互、Finder／剪贴板及账号启动／激活回归均**未验证**。未启动或退出 Claude，未修改真实登录数据。

## 1.7.2 联合发布（2026-10-28，v1.7.2）

- 按用户授权运行 `publish-release.ps1 -MacSize 1632956 -MacSha256 8ee751c3…771a`：Windows 1.7.2（更新进度窗口、中文安装进度、托盘图标、自有图标、长路径修复）与 macOS 1.7.2 一起发布：https://github.com/wyl20020808/ClaudeLauncher/releases/tag/v1.7.2 ，标签指向 dev `5cb6626`（包含两端改动）。
- 附件：`ClaudeLauncher-1.7.2.dmg`（1632956）、`ClaudeLauncherSetup-1.7.2.exe`（2793361）、两份清单。自建服务两份清单与 1.2.0 兼容 `latest.json` 已切到 1.7.2。
- 复核：脚本从自建服务与 GitHub 各下载两份清单和安装包验签核对通过；客户端代码从两个来源取到 Windows 1.7.2 并下载成功。
- 未验证：用户 1.7.1 → 1.7.2 应用内更新（1.7.1 仍用无界面的静默安装，新进度界面从 1.7.2 往后更新时才出现）、托盘实机行为、macOS 实机更新。

## macOS 1.7.2 发布准备（2026-10-28）

- 合并本地 dev（astra 项目管理 e1a44c4）与远端 Windows 托盘（66c3d96），STATUS 冲突保留双方记录。
- macOS 版本 1.7.2 / build 9，与 Windows 1.7.2 同版本联合发布（发布脚本联合模式按 Windows 版本打 v1.7.2 标签，DMG 文件名需同版本）。
- 内容：菜单栏常驻图标、自有图标、项目管理第一版。自检五组通过（arm64、x86_64）。
- DMG：ClaudeLauncher-1.7.2.dmg，size 1632956，sha256 8ee751c3741942cd8d9579fee490b93adbdaf5192511c6a3b6396a0b1aa4771a；签名 ClaudeLauncher Local Signing；自建更新地址已写入。已上传草稿 Release v1.7.2，等待 Windows 发布机签名并转正式。
- 计划由用户在 /Applications 的 1.7.0 中点击“检查更新”实测界面更新流程。

## 项目管理与菜单栏合并验证（2026-10-28）

- 按用户要求在 `feature/projects` 先合并 `origin/dev`（`001829f`）。解决 build.sh、main.m 启动初始化、STATUS 和使用说明的冲突：保留 Projects.m 编译、自有 AppIcon／菜单栏图标、构建前清空输出应用、菜单栏初始化以及项目清单损坏提示；双方历史记录保留。
- 独立验证构建：`SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh build/merge-projects-check`。编译、签名及 Info.plist 校验、arm64／x86_64 五组自检通过；证书要求保持不变，版本仍为 1.7.0 / build 8。没有重建顶层 build/ 中可能正在运行的应用，也没有替换 /Applications。
- 构建内 AppIcon.icns、menubar.png 与 resources/icon/ 源文件哈希一致，不含旧 Claude.icns。`build/merge-projects-check/projects.png` 离屏截图已查看，新 Logo、五项导航及项目表格正常。
- 相对 origin/dev 没有 Windows 文件变更；只纳入远端既有 Windows 更新。合成测试目录已清理。真实菜单栏点击、项目入口与 Finder／剪贴板／账号激活 GUI：**未验证**；未启动或退出真实 Claude，未操作登录数据。
- 本轮仅合并本地 dev，不推送、不打包、不发布。

## macOS 项目管理第一版（2026-10-28，feature/projects）

本节为当前开发状态；下方各轮记录及页首旧版汇总保留历史结论，不代表本轮重新验收。按用户要求先执行 `git pull origin dev`；远端已从用户提到的 `8952a13` 前进到 `604fe55`，从更新后的 dev 创建 `feature/projects`。先新增 SPEC“项目清单”，再实现。只修改 macOS 源码、构建脚本与文档，Windows 不动；版本仍为 1.7.0 / build 8，不打包、不发布、不安装到 /Applications。

### 实现

- 新增“项目管理”表格页，顺序为实例、账号、项目、日志、设置。支持添加已有目录、名称与分组、上移／下移排序、状态刷新、Finder 显示、复制路径、移除记录；长文本省略并提供完整提示。移除只删除清单记录，不删除项目文件。
- 清单全局共享，不绑定账号；上次账号仅作为选择默认值。选择任意 01～10 账号后复用 `selectAccount` 的 PID、保护、存储与启动检查，通过后复制路径并显示 Finder，再请求激活／启动，提示由用户在 Claude 中选择文件夹。取消或检查失败不交接项目。上次账号是通过检查后的请求记录，不是 Claude 加载成功状态。
- `src/Projects.h` / `src/Projects.m` 管理 `~/Library/Application Support/Claude Launcher/projects.json`，只保存目录入口；0600 临时文件写完后原子替换。损坏原文件改名保留并提示；未知版本、危险链接／权限或保留失败时停止写入。保存失败保持原清单和内存数据。
- 添加时规范化真实目录，拒绝账号目录、任何 ClaudeCodeConfig、启动器数据目录与应用包，拒绝链接绕过与重复目录。根目录／主目录需确认；路径失效只标记，不创建或重定向。不读取项目文件内容。
- SPEC、README、使用说明明确：项目本来就共享，本功能不复制／同步／合并内容，也不跨电脑同步；共享 CLAUDE.md、项目设置和 MCP 配置是用户选择的隔离取舍，同一项目请轮流使用，启动器不检测并发编辑。

### 构建与自检

- 已执行 `SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh`。通用二进制编译、Info.plist 校验、签名校验通过；arm64 与 Rosetta 下 x86_64 的五组自检均通过。
- 项目合成自检：受保护／普通／相对路径、别名与重复目录、JSON 往返与 0600 权限、名称／分组／排序／上次账号保存、写入失败保持原状态、损坏 JSON 与非法结构改名保留、未知版本不覆盖、链接／硬链接清单拒绝、无权限／失效路径、移除记录不删文件。只使用随机假主目录、假账号目录和假应用包，测试后清理。
- 原有账号隔离、参数／进程识别、保护互斥、并发存储检查及更新验签自检继续通过。`git diff --check` 通过。
- designated requirement 仍为 `identifier "local.claude.profile-launcher" and certificate leaf = H"5ce1d074d09ff41a6347a3bbcaff92c8fe25bda5"`，未换证书或改系统权限。没有更改版本号；构建仅增加 Projects.m 编译单元。未设置 UPDATE_BASE_URL，本轮 build/ 依现有脚本默认只用 GitHub 更新来源；已安装版本不受影响。

### 离屏截图核对

- 页码 2：`build/projects-empty.png`（空表）与 `build/projects-demo.png`（三行内存合成记录）已生成并人工查看。
- 已核对五项导航、共享取舍说明、六列表头、空状态、正常／不存在／无权限状态、上次账号、长名称／分组／路径截断和按钮禁用表现。图中均为演示路径，不读取／写入真实清单，不启动 Claude。图片位于 Git 忽略的 build/，不提交。

### GUI 与未验证项

- 实际 NSOpenPanel 选择、编辑／排序／移除菜单、主目录确认、剪贴板和 Finder 交接：**未验证**。
- 新入口对真实账号的激活／启动、最小化恢复、登录保护拒绝、检查期间路径变化与失败分支：**未验证**；自检不替代 GUI 验收。
- 重开图形应用后的清单恢复：**未验证**（独立存储对象读写往返已通过）。真实系统隐私拒绝提示、断开的网络卷及长期使用：**未验证**。
- 本轮未启动、退出、登出或删除真实 Claude 实例，未读凭据、进程环境或真实项目内容，未创建真实 projects.json；仅重建 build/，日常 /Applications 版本未替换。
- 当前功能没有待用户决定的阻塞项。后续 GUI 验收需正常退出旧启动器，再打开 build/ 中的开发版；不需要退出已运行的 Claude。Windows 项目管理留待后续按 SPEC 实现。
## Windows 托盘图标与自有图标（2026-10-28，feature/windows-tray）

- 托盘常驻：关闭主窗口只隐藏；左键单击/双击显示窗口，右键菜单（显示窗口 / 本机账号 / 检查更新… / 退出启动器）。菜单每次打开重新扫描进程，账号项与卡片主按钮同一流程；扫描失败时禁用并显示原因；不显示账号内容。安装程序改用专用退出消息（兼容旧版的 WM_CLOSE 仍保留），再次打开启动器用命名事件唤起已运行实例。
- 图标：`windows/assets/app.ico` 由用户图标 `resources/icon/icon_full_1024.png` 生成（8 个尺寸，16～24 为简化版）；exe、Setup、窗口、托盘统一使用。官方 `claude.ico` 与 `extract_icon.py` 已从 windows/ 删除（Git 历史中仍有）。
- 版本号未改（1.7.2，未发布）。
- 已执行：`build.ps1`（246 项断言，含托盘账号项）、`--self-test`（134 项）、`setup.ps1`、`test-install.ps1`（3 场景）；从 exe 与 Setup 提取图标核对为新图标。
- 未验证：托盘图标实际显示与清晰度、左键/双击/右键行为、菜单实时扫描与点击账号、关闭窗口后的提示、再次打开唤起窗口、安装程序通过专用消息关闭运行中的新版启动器、退出启动器。本会话未启动 GUI（用户已运行 1.7.1，启动新构建只会唤起旧实例）。

## 菜单栏常驻图标（2026-10-28，feature/menubar）

- SPEC 新增“常驻图标”（macOS 菜单栏、Windows 托盘，两端同一菜单）。
- macOS：NSStatusItem，占位图（SF Symbol，单色模板），菜单每次打开重新扫描：显示窗口、本机账号（与主按钮同一检查流程）、检查更新、退出启动器。保留程序坞图标。
- 构建与自检通过；新构建已运行。菜单栏图标是否可见、菜单点击：**未验证**（无截屏与界面操作权限，本机 macOS 27 的窗口列表中看不到状态栏窗口，需用户目视确认）。
- 图标：用户提供自有图标（源图背景已透明，无需抠图）。生成 AppIcon.icns（按 macOS 图标网格留白）、菜单栏单色模板图 menubar.png（36px，线条版：三层窗口 + 星芒）、Windows 用 icon_full_1024.png（无留白）；清除源图主体外的零散像素。构建改用自有图标，不再复制官方 Claude 图标，并在构建前清空输出应用。离屏截图确认侧栏 Logo 已更换。
- 待办：Windows 托盘与 .ico 由 Windows 端用 resources/icon/icon_full_1024.png 生成。

## Windows 1.7.1 发布（2026-10-28，windows-v1.7.1）

- 起因：用户在已安装的 1.7.0 中删除账号，报 PathTooLongException（Claude 缓存路径超过 260 字符）；删除在确认前的检查阶段停止，未移动任何数据。修复见 `a1e06f5`（声明 .NET 4.8 目标框架、longPathAware、遍历用 `\\?\` 前缀，不依赖系统长路径设置）。
- 用户决定两端版本不再要求一致（SPEC 已改）。每个正式 Release 仍须同时带两端清单与安装包；`publish-release.ps1` 不给 macOS 参数时只发布 Windows，从上一个最新 Release 验签带入 macOS 1.7.0 清单与 DMG。
- 已发布 https://github.com/wyl20020808/ClaudeLauncher/releases/tag/windows-v1.7.1 （目标 dev `57f6558`），自建服务 `latest-windows.json` 与 1.2.0 兼容 `latest.json` 切到 1.7.1，macOS 清单未动。脚本两来源复核通过；客户端代码从两个来源取到 1.7.1 并下载成功。
- 环境说明：Claude Code 运行在 Claude 桌面版（MSIX）中，本会话读到的 HKCU 卸载项被重定向，看不到用户真实安装（D:\Claude 多开启动器）的注册信息；此前的临时目录 Setup 测试也写在该重定向视图里。逻辑测试有效，但真实注册表下的覆盖安装需用户在已安装的 1.7.0 中点“检查更新”验证。
- 未验证：用户实机 1.7.0 → 1.7.1 应用内更新（下载、静默卸载旧版、安装到原目录、重新打开）；1.7.1 中实际删除长路径账号。

## macOS 1.7.0 端到端更新验证（2026-10-28，feature/mac-update-e2e）

用 `--update-test`（无对话框、自动接受，开发用）在临时目录的 1.6.0 测试副本上跑真实更新流程，未触碰日常应用：

| 项目 | 结果 |
|------|------|
| 自建服务取清单并验签 | 通过：HTTP 200，709 字节，验签通过，提示 1.7.0 |
| 自建服务下载与校验 | 通过：DMG 2412068 字节，大小与 SHA-256 一致 |
| 挂载、标识与版本核对、同证书检查、替换 | 通过：替换后 1.7.0，`codesign --verify --strict` 通过，签名要求不变 |
| 自建不可达回退 GitHub | 通过：自建地址改为不可达后回退 GitHub 并完成更新 |
| GitHub 跳转主机 | 仅 `github.com` → `release-assets.githubusercontent.com`，均在白名单 |
| 证书不一致拒绝 | 通过：ad-hoc 签名的 1.6.0 副本下载后被拒绝，保持 1.6.0，无暂存残留 |
| 临时文件 | 修复：成功更新后临时 DMG 未清理；替换脚本改为退出时删除自己的工作目录，复测无残留、无遗留挂载 |

- 替换后回滚（新版就位后校验失败）：真实发布包无法构造此失败，由此前替换脚本单测覆盖（签名损坏回滚），未做端到端。
- 新版启动后把旧版备份移到废纸篓：测试时日常启动器占用单实例锁，新副本启动即退出，备份未清理；真实场景下旧进程已退出，**未验证**。
- /Applications 已从 GitHub 正式 DMG 更新到 1.7.0：下载后 SHA-256 一致；`--check-signer` 同证书；签名身份 ClaudeLauncher Local Signing（leaf 5ce1d074…）；自建地址已写入；辅助功能 accessibility=yes；Claude 01（PID 99676）未受影响。旧 1.6 移到废纸篓。
- GUI 中点击“检查更新”未验证（无界面操作权限）；1.7.0 已是最新，下次发布 1.7.1 时可在界面实测。

## 1.7.0 发布（2026-10-28，Windows 发布机）

- Windows 已按 macOS 要求把 DMG 文件名改为 `ClaudeLauncher-x.y.z.dmg`（`550ff18`，自检 131 项通过）。
- 按用户授权运行 `windows/scripts/publish-release.ps1 -MacSize 2412068 -MacSha256 6fd16b9e…6280`：构建 Setup，从草稿下载并核对 DMG，签名 `latest-windows.json`、`latest-macos.json` 和 1.2.0 兼容 `latest.json`，上传自建服务与 GitHub，草稿转正式：https://github.com/wyl20020808/ClaudeLauncher/releases/tag/v1.7.0 （附件：DMG、`ClaudeLauncherSetup-1.7.0.exe`、两份清单）。
- 发布前经用户确认把草稿目标从 main（仍是 1.6 初始提交）改为 dev `5060f14`，标签 v1.7.0 指向该提交；未推送 main。
- 自建服务由旧项目 `D:\Claude-MultiLauncher\updates\server.py` 切换为 `windows/updates/server.py`，旧 `public/` 中的 1.2.0 文件已复制过来；隧道 kaskd 不变。
- 复核：脚本从自建服务与 GitHub 各下载两份清单和安装包，验签与哈希一致；另用客户端代码（含跳转白名单）从两个来源取清单并下载 Setup 成功；自建 `latest.json` 已指向 1.7.0.0，1.2.0 客户端可升级。
- 发布脚本修复（`bc8ecd6`）：包装函数 `Gh` 与 `gh` 同名导致递归，第一次运行在任何构建或上传前失败；改为直接调用 gh.exe 后重跑成功。
- 未验证：客户端实机点击“检查更新”并完成静默安装、1.2.0 客户端实际升级、macOS 客户端实际更新。Setup 与 exe 没有 Authenticode 签名。

## macOS 1.7.0 发布准备（2026-10-28，feature/mac-release-name）

- 草稿 Release v1.7.0 上传 DMG 时实测 GitHub 把附件名 `Claude多开启动器-1.7.0.dmg` 改成 `Claude.-1.7.0.dmg`：GitHub 回退下载会 404，Windows 发布脚本也取不到 DMG。macOS 发布文件名改为 ASCII `ClaudeLauncher-<版本>.dmg`（客户端校验、dmg.sh、SPEC 同步），已替换草稿附件。
- 正式 DMG：`ClaudeLauncher-1.7.0.dmg`，size 2412068，sha256 6fd16b9e8e252d9aa9febef88dc8f18df73c85e242bcf655406e8e9c8a3c6280；签名证书 ClaudeLauncher Local Signing（certificate leaf 5ce1d074…）；构建时设置自建更新地址（UPDATE_BASE_URL，与 Windows BaseUrl 相同）。从草稿回读下载，大小与 SHA-256 一致。
- 自检四组通过（arm64、x86_64）。
- 待 Windows：`Updates.FileName("macos")`、`updates/server.py` 白名单、`Storage.cs` 自检中的文件名改为 `ClaudeLauncher-x.y.z.dmg` 后再运行发布脚本。
- 未验证：真实端到端自动更新（等待 Windows 签名清单并转正式发布）；/Applications 仍为 1.6。

## Windows 1.7.0 更新协议（2026-10-28，feature/windows-sync）

上一轮交接内容（托盘重启动、递归后台存储检查、只显示已有账号、新建提醒、删除规则、Code 状态、登录保护、四页界面、Setup）已在 `ec71c69` 合入 dev，本轮在其上继续。只改 `windows/` 及 SPEC/STATUS。

- 版本 1.7.0（程序集 1.7.0.0）。
- `Updates.cs` 改为新协议：`latest-windows.json` 验签（RSA-3072 + SHA-256，沿用现有密钥）、自建 → GitHub 回退、“前往 GitHub 下载”、HTTPS 与跳转白名单、超时与大小上限、只升不降、`minimumOS`、下载核对失败即删除并换来源、运行前比对 Authenticode 签名者、静默运行 Setup；启动 8 秒后每 24 小时最多自动检查一次。保留 1.2.0 兼容的 `--apply-update`。
- Setup：`MinVersion=10.0.19041`；`/RELAUNCH=1` 时静默安装后重新打开启动器；先卸载再安装逻辑沿用上一轮。
- `updates/server.py`：白名单增加 `latest-windows.json`、`latest-macos.json`、`ClaudeLauncherSetup-x.y.z.exe`、`Claude多开启动器-x.y.z.dmg`，保留 `latest.json` 与 `ClaudeLauncher-x.x.x.x.exe`。
- 新增 `windows/scripts/publish-release.ps1`（取代 `publish-update.ps1`）：构建 Setup、下载并核对草稿中的 macOS DMG、签名两份清单与 1.2.0 兼容清单、上传自建服务与 GitHub 草稿、转正式、两来源复核，失败回滚。
- SPEC 标注【Windows 差异】：无代码签名时无法比对签名者；先卸载再安装导致新版安装失败时不能回滚旧版；`minimumOS` 每段 1～5 位。
- 已执行：`build.ps1`（241 项）、`--self-test`（130 项，含更新协议 32 项）、`test-install.ps1`（3 场景）、`setup.ps1` 及临时目录安装/覆盖/卸载、`publish-release.ps1 -DryRun`（测试密钥）、`server.py` 回环端口白名单测试、旧 `latest.json` 用仓库公钥验签通过。
- 未验证：真实公网更新端到端、正式发布路径（gh 上传/转正式/回滚）、启动器运行时 Setup 的关闭流程、`/RELAUNCH` 重新打开、有签名时的签名者比对、所有 GUI 点击。
- 发布前需要：发布私钥（本机 `%LOCALAPPDATA%\ClaudeLauncher-Publisher\signing-key.dpapi` 不存在）；自建服务恢复运行并确定地址（当前 502）；macOS 端上传 1.7.0 DMG 到草稿 Release 并给出 size/sha256。gh 已登录（wyl20020808）。

## Windows 同步到 1.6（2026-10-28，feature/windows-sync）

从 dev `5ccb1fc` 创建分支，只改 `windows/` 及 SPEC/STATUS 记录，未改 `src/`、`scripts/` 等 macOS 文件。详细对应与差异见 `windows/README.md`。

- 托盘重启动（使用该 PID 自身程序、5 秒后复查第二个主进程）已在 `c00dc9a` 完成，本轮保留。
- 存储校验递归到存储项内部（链接、硬链接、所有者），在后台执行；卡片显示“检查中”，完成后重新扫描并核对 PID 与保护状态，原 PID 退出/替换时不自动重启。
- 只显示本机已有账号、空状态、新建取最小未用编号；新建提醒三选项；“···”菜单（资源管理器显示、复制路径、登录保护、删除/重试删除）；回收站删除按 SPEC 收紧；保守 Code 标签；登录保护（持久化、准入、结束确认）。
- 界面按 `docs/assets/界面.png` 与 macOS 四页改为水墨浅色：实例管理、账号管理、日志记录（导出/清空可用）、系统设置（日志目录、数据目录、检查更新可用，其余静态占位）。插画构建时由 WebP 转 JPEG 内嵌。
- 版本 1.6.0（1.6.0.0）。新增 Inno Setup 安装包 `ClaudeLauncherSetup-1.6.0.exe`，按用户安装到 `%LOCALAPPDATA%\Programs`，已安装时先正常关闭启动器并静默卸载旧版再安装（SPEC 更新与安装），卸载不删账号数据；未做代码签名（无证书，SmartScreen 会提示）。
- Windows 待同步（SPEC 更新与安装）：应用内更新仍是 1.2.0 协议（自建 `latest.json`、单 exe 原地替换）。`latest-windows.json`、RSA-3072 公钥、GitHub Releases 回退、下载并静默运行 Setup 尚未实现，需与发布机密钥和发布脚本一起做。
- 已执行：`build.ps1 -Preview`（241 项断言、`--self-test` 98 项、四页离屏渲染）、`test-install.ps1`（成功/回退/篡改 3 个场景）、`setup.ps1` 及临时目录静默安装、覆盖安装（先卸载旧版）、卸载。
- 未验证：实际 GUI 点击与菜单、登录保护全流程、删除、托盘恢复、真实实例递归检查耗时、“Code 已指定”在真实 MSIX 启动时的表现、Code 实际写入、双账号登录、安装包快捷方式与交互向导、SmartScreen 实际提示。
- 待用户决定：代码签名证书；公开仓库中的自建更新服务器地址（`windows/src/Updates.cs`）；`windows/assets/claude.ico` 为官方图标，已在公开仓库并用作安装包图标。

## macOS 隔离加固（2026-10-28，feature/isolation-hardening）

本节是本轮最新状态；下方旧记录按其版本和测试日期解读，不代表本轮重新验证。已执行 `git pull origin dev`，从 `ad91581` 创建 `feature/isolation-hardening`。按用户要求只修改 `src/main.m`、`docs/`、`README.md`，不修改 Windows、构建脚本或 dev/main。版本号仍是 1.6 / build 7，不以版本号区分此次加固。

### 实现

- Code 状态改为“默认 / 待启动 / 未确认 / 已指定”。仅记录本次启动器向核实的新主进程指定独立配置的事实，不把目录存在或旧进程当成已隔离证据，不读取进程环境变量。重开启动器后已有进程保守显示“未确认”。
- 恢复“··· → 登录 / 重新登录保护…”；新建账号弹出提醒，可选“直接创建”或“进入登录保护”（2026-10-28 按用户要求由强制保护改为可选）。确认前后检查其他运行中、未管理、启动中或检查中的实例。保护持久化到启动器偏好；退出保护需要用户确认，受保护账号不能删除。不能拦截外部启动或系统登录回调。
- 存储递归检查移到后台，各账号独立等待、记录错误。同账号检查中禁止重复打开和删除；完成后重新核对进程、保护状态与根路径。原本要打开的 PID 在检查期间退出或更换时，不自动重启、不静默切换。扫描身份不明时仍全局暂停，以防重复启动。
- LaunchServices 请求超过 35 秒仍未回调时报告超时，但保留启动中占用，不允许重复启动或进入其他账号的登录保护，直到系统返回结果，避免迟到的启动请求干扰保护流程。
- 账号管理页增加登录和隔离说明，并补上原页面未加入窗口的问题；离屏截图改为建窗时选择目标页面，避免截图前切页造成不完整渲染。
- 固定账号目录、01 默认 Code 配置、既有登录数据保持原样。没有读取或修改凭据、读取进程环境变量、退出/登出/强杀 Claude、修改系统权限或迁移账号数据。

### 自检与构建（已执行）

- 使用 `SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh build/isolation-hardening-check`：编译、Info.plist 校验、签名校验与合成自检通过。
- 自检覆盖：原有路径/参数/删除规则，以及登录保护准入、未管理进程与重复主进程、其他账号进行中操作、受保护账号删除拦截、检查期间保护变化和 PID 退出/替换、保守 Code 状态、两个合成账号并发元数据检查及主线程回调、深层 Code 存储链接拒绝。随机测试目录已清理；没有创建真实账号。
- `git diff --check` 通过。

### 界面检查与 GUI 验收

- 离屏渲染：实例页、账号管理说明页已生成并人工核对，文字未被裁切。这只是渲染检查，不是实际点击或登录测试。图片位于 `build/isolation-hardening-check/`，未提交。
- 实际 GUI 点击、登录保护确认/取消与重开保留、检查中连点及删除菜单互斥、正常激活/最小化恢复：**未验证**。
- 新 Claude 进程的 Code 实际写入、真实双账号登录/重新登录、回调归属及彼此登出影响：**未验证**。本轮没有启动或退出真实 Claude 实例，也没有调用真实删除流程。

### 签名与交付

- 已执行用户指定命令 `SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh` 重建日常 `build/Claude 启动器.app`；最终源码编译、Info.plist 校验、签名校验和全部合成自检通过。
- 独立验证构建与重建前日常应用的 designated requirement 相同：`identifier "local.claude.profile-launcher" and certificate leaf = H"5ce1d074d09ff41a6347a3bbcaff92c8fe25bda5"`，使用用户指定固定证书，不使用 ad-hoc。代码哈希会变化；本轮没有重新操作辅助功能授权，授权实效仍需 GUI 验证。
- 不发布、不合并 dev/main。需要用户退出并重新打开启动器才能使用新代码，已有 Claude 实例不会随启动器重建自动重启。

## 1.6 变更（2026-10-28）

- 02～10 启动时设置 `CLAUDE_CONFIG_DIR=<账号目录>/ClaudeCodeConfig`（不存在时以 0700 创建），隔离 Code 标签页的 `~/.claude` 与 `~/.claude.json`。01 不变。依据：官方 4.29.0 程序包只读检查显示桌面端读取 `process.env.CLAUDE_CONFIG_DIR`，并提示可在桌面设置中配置该变量。
- `ClaudeCodeConfig` 纳入存储元数据校验（拒绝链接、硬链接、非本人所有）。
- 删除状态文本中两分支相同的多余三元表达式。
- 构建与内置自检通过（新增配置目录映射断言）。未发布到 releases，未替换日常应用。
- 未验证：真实实例是否把该变量传给 Code 标签页（需重启已登录账号实例，未执行，未触碰登录数据）。已运行实例需正常退出后从启动器重新启动才生效；该账号此前在共享 `~/.claude` 中的 Code 记录不迁移、不删除，仍留在原处。
- 签名：初次为 ad-hoc；后续改用固定证书，见下方第 0 项。

## Windows 整合（2026-10-28，windows-merge）

- 原独立 Windows 启动器（发布版 1.2.0，C# WPF）源码并入 `windows/`，按 SPEC 补齐：`CLAUDE_CONFIG_DIR`、严格参数解析、同账号多主进程报错、35 秒启动超时、存储元数据校验、`--self-test`；删除入口隐藏。
- 构建、自动检查（241 项）和 `--self-test`（60 项）在 Windows 11 实机通过。GUI 与真实实例未验证。实测差异见 `windows/README.md` 和 SPEC 中的 [Windows 差异] 标注。
- 2026-10-28 规格定稿：6 项差异按 SPEC 当前文字确认。Windows 待适配：托盘重启动须使用该 PID 自身的可执行文件（当前用扫描到的首个实例路径），并在重启动后重新扫描，同账号出现第二个主进程时报错（当前未单独检查）。（已在 `c00dc9a` 完成，见上方 Windows 同步记录。）
- 待用户决定：公开仓库含自建更新服务地址（Updates.cs、updates/维护说明.md）和官方图标 windows/assets/claude.ico。

## 界面改版（2026-10-28，feature/ui）

- 按用户参考图改为水墨风：左侧导航四项（去掉参考图中的“5小时限额”“周限额”导航），账号卡片含编号圆标、状态点、目录与 Code 配置标签、插画、主按钮和“···”菜单。
- 卡片右侧限额区域按用户要求保留为空占位并标“未接入”：数据需读取账号凭据，项目约束禁止；参考图中的地区标签未实现。
- 新增“+ 新建实例”、日志记录页（记录底部提示）、系统设置页、`--snapshot` 开发截图参数。界面固定浅色。
- 用户手动实测（01:15 起）：导航、滚动、更多菜单、打开 01 基本正常；01 未重复启动；从新版启动 02 正常，ClaudeCodeConfig 以 0700 创建；“+ 新建实例”创建了 05。复制路径一项未确认。
- 改为只显示本机已有账号，摘要显示“本机 N 个账号 · M 个运行中”。
- 2026-10-28 按用户明确要求清理（均移到废纸篓，可恢复）：旧启动器副本 5 个（Documents/Codex 下 4 个、releases/1.5）；账号目录 Claude-Profile-03、04、05（无运行实例）。保留 01、02 和 build/ 中的当前版本。releases/ 目前为空，日常请用 build/Claude 启动器.app。

## 页面视觉改版（2026-10-28，feature/ui-pages）

- 全部页面使用水墨樱花背景（resources/ui/background.webp），左侧淡化以保证可读性。侧栏增加版本行。
- 账号管理：统计卡（账号总数、运行中、需要处理，均为真实本地状态）、搜索与状态筛选（静态占位）、刷新状态、账号表格（对应实例、运行状态、目录修改时间作为最近使用、“···”菜单可用；编辑按钮为占位）。登录邮箱列显示“不读取”：启动器不读取凭据或账号信息。
- 日志记录：记录本次运行的提示，自动分信息/警告/错误并识别账号；导出日志（保存为文本）与清空日志可用；时间、实例、级别筛选、搜索和翻页为静态占位，只显示最新 10 条；日志不持久化。
- 系统设置：分类导航与各开关、下拉为静态占位；“关闭窗口后继续运行”反映实际行为；辅助功能权限状态与“打开设置”、数据存储路径（点击在 Finder 中显示）可用。
- 构建、自检通过；四个页面离屏截图核对。真实点击未验证。

## 安装包（2026-10-28，feature/dmg）

- 新增 scripts/dmg.sh，生成 build/Claude多开启动器-1.6.dmg（约 1.9 MB，签名、hdiutil verify 通过）。
- 构建改为通用二进制（arm64 + x86_64）；两种架构自检通过（x86_64 经 Rosetta）。
- 已从 DMG 安装到 /Applications/Claude 启动器.app 并启动；辅助功能授权保留（accessibility=yes）。
- 未做 Apple 公证：客户首次打开需手动放行。图标与名称对外分发的许可待用户确认。
- Windows Setup 安装程序列为 Windows 端待办（见 windows/README.md）。

## 更新与安装规格（2026-10-28，feature/update-spec）

- SPEC 新增“更新与安装”：自建服务优先，失败回退 GitHub Releases，两处都失败引导手动下载；按平台签名清单（RSA-3072 + SHA-256，同一把密钥）；只升不降；安装前先卸载旧版（只卸程序，不动账号数据），失败回滚；新旧版本须同一证书。
- 待办 Windows：服务端与清单改为多平台并保留旧 latest.json 兼容；发布脚本（签名、上传自建服务与 GitHub Release、回读校验）；Inno Setup 先卸载再安装；Updates.cs 对齐新协议与回退。
- 待办 macOS：应用内检查更新、验签、DMG 下载安装与回滚、签名要求一致性检查、回退与手动下载入口。尚未实现。

## macOS 自动更新（2026-10-28，feature/mac-update，版本 1.7.0 / build 8）

- 实现 SPEC“更新与安装”的 macOS 客户端：自建服务（可选，构建时配置）→ GitHub Releases → 手动下载；签名清单校验、下载校验、同证书检查、退出后替换与失败回滚、旧版移到废纸篓。
- 自检新增：临时密钥签名与篡改拒绝、XML 公钥导入、版本比较、跳转白名单、安装位置、内置公钥为 RSA-3072；arm64 与 x86_64 均通过。
- 实测：替换脚本在临时目录用假应用包验证了成功、签名损坏回滚、越界路径拒绝；`--check-signer` 接受同证书版本，拒绝系统应用和 ad-hoc 重签副本。
- 未验证：真实端到端更新（需要 Windows 发布机用私钥签出 latest-macos.json 并发布到 GitHub Release；目前尚无 Release，GitHub 回退会得到 404 并提示手动下载）；自建服务地址尚未配置。
- 构建版本升到 1.7.0，DMG 为 build/Claude多开启动器-1.7.0.dmg；/Applications 中仍是 1.6，未替换。

## 已实现

- 10 个固定账号目录；01 沿用原默认目录。
- 官方主进程与目录识别、PID 定向激活、启动防重入、启动器单实例。
- 路径与核心存储元数据校验，不读取登录凭据。
- 原生精简界面、三种按钮配色、官方原版图标。
- 构建及内置自检；中文使用说明和历史实测记录。

## 未解决或未验证

0. 稳定签名（2026-10-28，feature/stable-signing）：build.sh 支持 `SIGN_IDENTITY`。用临时钥匙串中的一次性自签名测试证书实测：两次构建 requirement 均为 `certificate leaf = H"…"`，不含 cdhash，codesign 校验与自检通过；测试钥匙串已删除，用户钥匙串搜索列表未变。同日用户创建正式证书「ClaudeLauncher Local Signing」并为 build/ 中的 1.6 授权辅助功能后实测：临时修改代码重建，CDHash 由 a9fcb638… 变为 be38fd1e…，授权仍为 yes；还原代码重建后仍为 yes。授权身份稳定问题已解决。最小化窗口恢复的 GUI 实测仍未执行（需退出旧版启动器后运行 1.6）。
1. （1.5 ad-hoc 版本，已由第 0 项的证书签名解决，保留原记录）辅助功能授权不生效：2026-10-28 重启 1.5 后仍报告未授权。当前签名按代码哈希识别，前几轮重建使身份变化；尚未读取系统授权数据库确认旧条目具体匹配关系。最新版本的最小化恢复仍未通过实测。
2. （已由第 0 项解决）没有稳定开发签名身份。需要解决后续 UI 重建导致重新授权的问题，再完成窗口恢复验收。
3. 两个真实账号登录、回调归属及相互登出影响未验证。不能保证完全隔离。仍共享应用级加密设施、`claude://` 登录回调、~/Library/Logs/Claude、用户项目文件、网络出口等。不能仅因系统钥匙串共用就推断账号令牌相同；Code 的独立配置需实际验证。目录分离不是安全沙箱，独立系统用户也不等于完全独立的电脑。
4. 深色模式、完整键盘操作及旧版登录保护状态迁移界面未实测。
5. 删除账号：2026-10-28 按用户要求实现（移到废纸篓，01 与运行中账号不可删，二次确认）。自检覆盖删除拦截条件。用户实测删除账号 03：目录已从 Application Support 移除，界面回到“本机 2 个账号”，启动器与 01 进程不受影响；废纸篓内容受系统隐私保护无法从终端核对，由用户在 Finder 确认。

## 兼容行为

1.3 至本轮加固前已移除“单独登录”入口，仅保留旧保护的退出路径。本轮已恢复显式保护入口并沿用同一偏好键，详见最新记录。

## 后续顺序

签名身份稳定性已解决；下一步在用户配合下验证本轮 GUI、登录保护与准确 PID 激活，再验证真实双账号及 Code 实际写入。删除已按用户要求实现，本轮只增加检查和保护互斥，不自动删除数据。后续修改日常 build/ 必须提前提醒，并继续使用固定证书。

历史记录保留原样，其中 1.1 的登录保护入口等描述不代表 1.5 界面。项目整理不声称新增通过任何 GUI 或真实登录测试。

## 项目整理验证

2026-10-28：在新的项目结构内实际运行 build.sh，编译、内置合成自检、Info.plist 和签名校验通过；package.sh 生成 ZIP 并通过完整性检查。发布副本与原 outputs 应用可执行文件相同，原应用未重建。此轮未重新运行 GUI 恢复或登录测试。
