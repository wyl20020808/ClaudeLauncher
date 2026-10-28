# 项目状态

整理日期：2026-10-28。源码与本机日常 build/：1.6 / build 7（未发布）；历史交付：1.5 / build 6。

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
