# 开发与验证

## 实现入口

`src/main.m` 包含目录映射、进程参数解析、主进程扫描、存储路径校验、AppKit 窗口、启动与 PID 激活、自检。`src/Projects.h` / `src/Projects.m` 包含项目清单与合成测试，由同一 clang 命令编译；不引入框架封装或第三方依赖。

- `ProfilePath`：固定编号到目录的映射。
- `CodeConfigPath`：02～10 的 Claude Code 配置目录，启动时作为 `CLAUDE_CONFIG_DIR` 传入；01 返回 nil，沿用 `~/.claude`。
- `Scan`、`AccountForArguments`、`IsMainExecutable`：区分官方主进程、辅助进程及 CLI。
- `ValidateProfileAtPath` / `ValidateProfileAsync`：完整递归元数据检查及后台调度；回调返回主线程。`ValidateProfileRoot` 用于操作前快速复核固定根路径。
- `LoginBlocker` / `openingBlocker:expectedPID:`：登录准入、检查完成后的进程与保护状态复核；`checking` 集合按账号防止检查期间重复操作。
- `configuredApps`：仅保留本次启动器返回的、启动时间与主程序/账号参数已核对的新进程对象。它证明已指定配置，不证明 Code 实际写入，不能持久化 PID 后冒充已验证状态。
- `Launcher`：界面、启动状态与单实例管理。
- `activatePID:account:prepare:`：校验 PID 后激活、尝试恢复窗口；可选项目交接在身份确认后执行。
- `SelfTest`：合成数据测试，不读取真实凭据。

## 构建

运行 `scripts/build.sh`，默认输出到项目 build/。可传入相对项目根目录或绝对输出路径。不要指定日常正在运行或已授权应用所在目录。

构建执行 clang、Info.plist 校验、临时本地签名、签名校验和内置自检。应用图标与菜单栏图标来自 resources/icon/（用户自有图标，源图 icon-source.webp），构建不再依赖本机 Claude.app 的图标；每次构建先清空输出应用，拒绝直接写入 /Applications。版本号与 build 号位于脚本生成的 Info.plist 内。

单独执行自检：

```sh
./build/'Claude 启动器.app'/Contents/MacOS/ClaudeLauncher --self-test
```

自检在当前工作目录创建随机合成目录并清理，仅覆盖参数解析、路径及存储校验等逻辑。不能代替真实窗口测试。

隔离加固自检增加登录准入、检查中删除拦截、保护状态变化、原 PID 退出/替换、保守 Code 状态、并行后台检查的主线程回调，以及深层 Code 存储链接拒绝。并发测试只检查随机合成目录，失败不会污染另一个合成账号的结果；不创建真实账号，不读写登录数据。

本轮按用户要求执行 `SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh`，版本号仍为 1.7.0 / build 8，以分支和提交区分。重建前提醒用户；build/ 的变化不会替换正在运行的进程，也不会更新 /Applications 中的日常版本。本轮不打包、不安装、不发布。

## 项目清单实现与验证

- `ProjectStore` 只管理启动器自身的 JSON：`load`、`save:error:`、`normalizeDirectory:error:`、`statusForPath:`、`containsDirectory:`。格式、上限与取舍见 SPEC“项目清单”。最多 1000 条，文件最多 1 MiB，名称／分组最多 200 字。
- 写入使用同目录 0600 独占临时文件，写完并 fsync 后 rename 原子替换；失败不提交内存模型。读取前检查各级目录，文件以 O_NOFOLLOW 打开并 fstat 拒绝非本人所有、链接或开放权限的文件。已有不安全文件不 chmod、不读取内容。
- 损坏内容以唯一名字改名保留，恢复失败或未知版本使存储只读；绝不把清单里的路径当成要删除的目录。手动添加使用 NSOpenPanel 和元数据检查，状态检查不递归读取项目。
- `selectAccount:` 委托给 `selectAccount:prepare:`，普通账号操作传 nil；项目入口传一个准备回调，经原有后台检查、PID／保护复核后执行。新建账号的“进入登录保护”也传递同一回调。回调再次核对项目仍存在且目录可用，持久化 lastAccount，再复制路径、显示 Finder，随后由原流程激活／启动账号。lastAccount 是已请求操作，不是加载成功凭证。
- 编辑对话框期间后台账号检查可能完成；保存编辑时只更新名称／分组，保留最新 lastAccount。移除按稳定 UUID 定位，避免后台状态更新导致移除失效。
- `TestProjects` 集成进 `--self-test`：仅在当前工作目录的随机 `.claude-project-tests-*` 中生成假主目录、假账号／配置目录与假应用包；覆盖路径、别名、重复、权限、JSON 往返、编辑排序与账号记录、损坏保留、未知版本保留、链接清单拒绝和移除不删文件。测试结束清理。chmod 仅用于随机合成目录的无权限用例，不修改真实账号或系统权限。
- GUI 手工验收：添加／取消、主目录确认／取消、重名与重复目录、编辑排序、移除后文件仍在；正常／缺失／无权限目录；在已有账号间选择、默认上次编号、保护拒绝、后台检查失败、取消创建；确认失败分支不改变剪贴板或打开 Finder。保存工作后仅用少量实例，不测试真实账号删除或登出。

## 项目导入实现与验证

- `src/ProjectImport.h` / `.m` 编译进同一个原生应用，不增加运行时依赖。`DiscoverProjects` 从固定账号目录枚举三层；不依赖账号进程，覆盖未运行账号。后台使用独立的只读 `ProjectStore` 路径校验对象，不跨线程读写界面清单。
- 数据来源由官方 4.29 的只读包内代码调查确定：序列化记录开头依次为 `sessionId`、可选 `cliSessionId`、`cwd`、`originCwd`，目录为 `claude-code-sessions/<account UUID>/<org UUID>/local_<UUID>.json`。`ReadPathPrefix` 每次 read 1 字节，最多 32768 字节，读完 originCwd 后的分隔符立即停止。未知字段连值都不读；不整文件 JSON 反序列化，不进入正文或配置。不扫描 CLI 的 projects、JSONL、.claude.json、浏览器数据库或日志。
- 安全打开：逐层 openat + O_NOFOLLOW，目录和普通源文件归属当前用户且不可被其他用户写入；源文件必须单硬链接。读取前后核对大小与修改时间，变化则跳过。目录项总数 20000、记录总数 5000 的上限明确报告。官方格式变化时保守跳过，不扩展到任意字段搜索。
- `PrepareProjectImport` 只构造追加后的清单，不写磁盘。确认时复核路径未重定向、可访问且未重复，名称来自目录名，lastAccount=0；上限与原子保存复用 `ProjectStore`。取消、全部重复、无选择不写入，保存失败保留原清单。
- `TestProjectImport` 只生成随机 `.claude-import-tests-*` 假主目录／假账号记录。覆盖：多账号离线记录、跨账号去重、来源合并、originCwd 优先、缺省 CLI ID、Unicode 与转义路径、未知字段与坏前缀、读取偏移恰好停在正文前、输入上限、符号／硬链接及开放写权限拒绝、路径消失后复核、取消不写入、批量往返、保留编辑和非破坏移除。合成权限用例不修改系统或真实账号权限。
- 只读真实兼容性诊断：`ClaudeLauncher --project-import-check`。不载入／写入真实 projects.json，只输出各账号计数和失败类别；不输出目录、账号 UUID、记录 ID 或正文。不启动 GUI，不请求更新或官方接口。此项与合成自检、GUI 分开报告。
- 离屏截图：`ClaudeLauncher --snapshot build/project-import-check/projects.png 2 --projects-demo` 核对主页面；`--snapshot build/project-import-check/import.png 2 --import-demo` 使用合成来源与目录绘制导入预览，不读真实账号。系统弹窗的部分原生控件不能由离屏缓存完整捕获，不能把附件区域截图当作完整弹窗 GUI 验证。
- GUI 待验收：识别时页面仍可操作、取消／取消全选不写入、确认批量保存、重复导入、路径在预览期间失效、容量不足／写入失败；核对各账号来源和滚动长列表。识别与导入不应启动／激活 Claude，不应改变剪贴板或打开 Finder。
- 本轮基于 dev `5cb6626`，保持 1.7.2 / build 9；固定证书构建到 `build/project-import-check/`，不覆盖顶层 build/ 或 /Applications，不打包发布。

## 原生侧栏目录登记

- `RegisterProjectDirectories` 位于 `src/ProjectImport.m`，复用只读前缀解析与安全目录打开。输入只有待登记路径，固定七字段序列化顺序，生成每目标独立 UUID；不会反序列化或复制来源的完整会话文件。
- `RegistrationDirectory` 只接受唯一账号／组织 UUID 目录，不创建身份位置。目录 flock 拒绝并行登记；扫描现有记录只读取目录前缀，未知前缀、链接、硬链接、权限、扫描不完整均拒绝。目录元数据变化也视为失败。
- 写入前进行路径白名单与 inode 去重；0600 独占临时文件写入、fsync，然后 `renameatx_np(RENAME_EXCL)` 原子发布，拒绝覆盖。每个文件发布前重新打开目标位置核对 inode、重新检查项目路径及进程。中途失败返回已完成计数，不删除已有成功记录。
- `RegistrationGuard` 核对 Claude 4.29.0、登录保护、目标主进程和固定 profile 根；`RegisterForAccount` 加上原有完整存储元数据检查。GUI 操作期间复用 `checking` 集合，阻止目标账号在启动器内被启动／删除；不能阻止用户从其他入口启动，需在提示中明确。
- `--register-projects-from <源编号> <目标编号>` 为只读计数预览；追加 `--apply` 才登记。源只扫描指定账号目录前缀，不读凭据、不输出会话 ID 或正文；目标执行与 GUI 相同的检查。该命令不能代替 GUI 验收。
- `TestProjectRegistration` 纳入原生 `--self-test`：合成目录覆盖精确字段白名单、Unicode／转义路径、独立 ID、0600、前缀兼容、无写入预览、重复重试、部分成功、运行中拒绝、路径在发布前变化、多组织、未知前缀、符号／硬链接及并行写入拒绝。所有测试数据均在随机合成目录内并清理。
- 产品不依赖开发探针的 Node，也不打包探针；正式实现使用 Foundation／POSIX。GUI 不修改浏览器数据库或目录信任，用户需在 Claude 中开启空分组。共享文件／规则的取舍不变。

本轮固定证书构建目录为 `build/project-registration/`，原生与 x86_64 自检均为七组通过；真实 01 → 02 验收见 STATUS。

## GUI 验收

原生侧栏空记录的开发探针：`node scripts/check-sidebar-seed.cjs`。只访问本机官方包的固定哈希代码和随机合成目录；不访问真实账号、不启动 Claude、不写入产品构建。它验证记录白名单、独立 ID、0600 往返及实际纯分组函数，不能替代客户端加载与 GUI 验收。单账号受控实验、清理流程及未验证项见 [本地侧栏调查第二阶段](SIDEBAR-LOCAL-RESEARCH.md#第二阶段全新空归档记录用户追加授权)。此探针不是日常运行依赖，不提供向真实账号写入的命令。

在用户授权范围内，仅使用少量实例：验证打开运行中账号不新增进程；未运行账号正确启动；同账号连点不重复启动；隐藏和最小化后恢复正确 PID；重开启动器只显示一个管理窗口；退出启动器后 Claude 继续运行。首次创建测试须使用确认未使用的账号目录，不登出、删除或覆盖真实账号数据。

登录测试需用户自行登录；没有真实双账号验证时，不得宣称登录不会串号。历史报告中的通过项不自动适用于新构建。

## 打包与签名

`scripts/package.sh` 仅校验并压缩已构建应用，不重新签名或启动应用。修改应用后先重新构建，验证后再打包。

未设置 `SIGN_IDENTITY` 时使用 ad-hoc 签名，designated requirement 是代码哈希（cdhash），每次代码变化都会使辅助功能授权失效。

稳定签名：用固定证书签名后，requirement 变为 `identifier "local.claude.profile-launcher" and certificate leaf = H"<证书指纹>"`，与代码无关，重建后授权仍匹配。

一次性准备（用户自己操作，启动器和脚本不会创建证书或修改钥匙串）：

1. 打开「钥匙串访问」→ 菜单「钥匙串访问」→「证书助理」→「创建证书…」。
2. 名称填 `ClaudeLauncher Local Signing`，身份类型「自签名根证书」，证书类型「代码签名」，创建到「登录」钥匙串。
3. 构建：`SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh`。首次签名时系统询问是否允许 codesign 使用该密钥，选「始终允许」。
4. 在系统设置中给这个构建授权一次辅助功能（需移除旧的 ad-hoc 条目）。以后用同一证书重建不应再需要授权。

检查授权须经 LaunchServices 启动，否则系统按终端等父进程判断：`open -W -n --stdout /tmp/diag.txt "build/Claude 启动器.app" --args --diagnose`，查看输出中的 `accessibility=`。

`security find-identity -v` 会把自签名证书列为不受信任，这不影响 codesign 签名，无需改信任设置。构建输出会打印 designated requirement，应看到 `certificate leaf` 而不是 `cdhash`。证书丢失或更换后需重新授权。`SIGN_KEYCHAIN` 可指定证书所在的钥匙串文件，通常不需要。

不要通过修改 TCC 数据库、放宽签名要求或绕过权限解决授权问题。

## 界面截图

`ClaudeLauncher --snapshot <输出.png> [页码]` 在后台渲染窗口到 PNG，不占单实例锁、不启动 Claude。页码：0 实例管理、1 账号管理、2 项目管理、3 日志记录、4 系统设置。从终端运行时辅助功能状态按终端判断，可能显示未授权。插画位于 `resources/ui/`，构建时复制进应用。

项目截图不读取／写入真实清单，默认空表；加 `--projects-demo` 使用内存合成的三行（正常、目录缺失、无权限，含长名称与路径）：

```sh
"build/Claude 启动器.app/Contents/MacOS/ClaudeLauncher" --snapshot build/projects-empty.png 2
"build/Claude 启动器.app/Contents/MacOS/ClaudeLauncher" --snapshot build/projects-demo.png 2 --projects-demo
arch -x86_64 "build/Claude 启动器.app/Contents/MacOS/ClaudeLauncher" --self-test
```

离屏截图只核对布局与文案，不替代 NSOpenPanel、菜单、剪贴板、Finder 或真实账号激活的 GUI 测试。

## DMG

`scripts/dmg.sh [应用路径] [输出路径]` 把已构建应用和“应用程序”快捷方式打进只读压缩 DMG（UDZO），设置 `SIGN_IDENTITY` 时同时给 DMG 签名，最后 `hdiutil verify`。不重新构建、不修改应用。构建为通用二进制（arm64 + x86_64），Intel 自检可用 `arch -x86_64 … --self-test` 在 Rosetta 下运行。未做 Apple 公证；macOS 27 上 `hdiutil create/attach` 会提示已弃用但仍可用。

## 自动更新（macOS）

规则见 SPEC“更新与安装”。实现要点：

- 来源：构建时 `UPDATE_BASE_URL=https://…` 写入 Info.plist 的 `ClaudeLauncherUpdateBaseURL`（可不设）；之后固定回退 GitHub Releases `latest/download/`。都失败时提示并提供“前往 GitHub 下载”。
- 公钥：构建时复制 `windows/assets/update-public-key.xml`（RSA-3072，与 Windows 同一把）。私钥只在 Windows 发布机。
- 校验：清单形状 + RSA-SHA256 签名 → 版本只升不降、系统版本 → 下载包大小与 SHA-256 → 挂载 DMG，核对包标识、版本 → 新应用须满足当前应用的签名要求（同一证书）→ 复制到同目录隐藏的暂存包并再次核对。
- 替换：`resources/update-helper.sh` 等启动器退出后，旧版改名备份、新版就位并 `codesign --verify`，失败则回滚旧版；新版启动后把备份移到废纸篓。只动这三个应用包。
- 入口：系统设置“检查更新”、应用菜单“检查更新…”；启动 8 秒后若距上次检查超过 24 小时自动检查一次（失败只记日志）。
- `ClaudeLauncher --check-signer <应用路径>`：开发用，判断该应用能否作为更新被接受。
- `scripts/dmg.sh` 输出发布机签名清单需要的 `file`、`size`、`sha256`。
- `ClaudeLauncher --update-test [--github-only]`：开发用。不弹对话框、自动接受更新，打印每个请求、跳转主机和结果，然后走真实下载与替换。只在临时目录的测试副本上运行（可把副本 Info.plist 的版本调低、改 `ClaudeLauncherUpdateBaseURL` 后用同一证书重签），不要对日常应用运行。

## macOS 1.7.3 发布交接

应用构建与 DMG 签名在 Mac 执行，自动更新清单仍使用 Windows 发布机的原 RSA 私钥；Windows 软件不更新、不构建。具体产物、验签原文、只切换 macOS 清单及失败回滚要求见 [RELEASE-macos-1.7.3.md](RELEASE-macos-1.7.3.md)。签名清单和两端附件齐全且验签通过后才将草稿设为 Latest。

## macOS 独立发布

- 密钥：`scripts/release-key.m`（编译为 `build/release-key`，用固定证书签名，标识 `local.claude.profile-launcher.release-key`）。`create` 在登录钥匙串生成 RSA-3072 私钥，**不可导出**（创建后立即尝试导出，若成功则删除并报错）；`public` 输出公钥 XML；`sign` / `verify` 用于清单。私钥不打印、不进仓库或 DMG。注意：“不可导出”必须写在 `SecKeyCreateRandomKey` 的顶层参数，只写在 `kSecPrivateKeyAttrs` 中会被忽略（实测）。
- 发布：`scripts/publish-macos.sh [--dry-run]`。要求工作区干净、当前提交已推送、`gh` 已登录、钥匙串公钥与 `resources/update-public-key-macos.xml` 一致；版本必须高于 GitHub 上当前的 `latest-macos.json`。流程见脚本头注释与 SPEC。上传后先经 API 核对草稿附件；转为 Latest 后最多等 10 分钟 latest/download 缓存。仅内容不符才回滚（改回草稿并恢复上一版 Latest）；缓存未及时更新时保留发布、以状态 2 退出，稍后手动复核即可。自建服务由 Windows 端定时同步，脚本不操作。
- 包内 Mac 公钥文件名为 `macos-publisher-key.xml`（不能以 `-macos` 结尾，见 SPEC【macOS 差异】）。

## 本地设置与控件验收（feature/local-settings）

- `src/Settings.m/.h` 管理默认值、类型／范围白名单、本地别名校验、日志组合筛选及更新 URL 白名单；`TestSettings` 合入 `--self-test`。
- 构建加入系统 ServiceManagement.framework，无第三方依赖。固定证书构建到 `build/local-settings/`，保留已安装应用和其他开发构建。
- 运行 `build/local-settings/'Claude 启动器.app'/Contents/MacOS/ClaudeLauncher --ui-self-test build/local-settings/checks`：在随机独立偏好 suite 中进行原生控件 target/action 测试，结束清理 suite；只用合成账号／日志，不运行正常启动生命周期、不扫描真实账号、不启动 Claude、不注册登录项，不创建网络会话。
- UI 自检覆盖六类切换、草稿与保存／重载、撤销、默认值保存不清除其他偏好、关闭窗口返回值、插画／用量／通知开关、日志数量上限／组合筛选／分页、账号搜索／状态／全选／部分选中及隐藏选择清理。生成六张设置页和账号／日志页的离屏 PNG。
- 网络拒绝测试直接调用 Fetch：更新关闭时拒绝合法更新地址；开启时拒绝 Anthropic 地址，两个分支都在 NSURLSession 创建之前返回。URL 测试另覆盖协议、域名伪装、其他仓库、用户信息和端口；不对远端发探测请求。
- 合成测试和离屏渲染不等于真实系统授权、登录启动、菜单栏、Finder、文件选择或真实 Claude 窗口恢复验收。这些系统集成保留人工验收边界，不为测试自动修改用户系统设置。
- `--update-test --allow-updates` 是明确联网并可自动安装更新的开发命令，本轮未执行；无参数调用必须直接返回 2，不能发请求。发布脚本不再把任意自建更新地址注入 macOS 包。
