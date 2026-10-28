# 跨平台行为规格

macOS（`src/main.m`）和 Windows（`windows/`）各自原生实现，共同遵守本规格。macOS 1.6 是参考实现；规格变化时两端同步修改并在 STATUS 记录。

## 账号与目录

| 编号 | 数据目录 | Claude Code 配置 |
|------|----------|------------------|
| 01 | 官方默认 userData（位置由平台和安装方式决定），不传覆盖参数 | 官方默认（`~/.claude`） |
| 02 | `<基础目录>/Claude-Second` | `<数据目录>/ClaudeCodeConfig` |
| 03～10 | `<基础目录>/Claude-Profile-NN` | `<数据目录>/ClaudeCodeConfig` |

- 基础目录：macOS 为 `~/Library/Application Support`；Windows 为 `%APPDATA%`（沿用已有 02/03 数据）。
  - **[Windows 差异，已确认]** MSIX 版 01 的默认 userData 实测为 `%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc`，不在基础目录下；商店版还可能把目录虚拟化到 `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\<名称>`，Windows 端按同一账号识别和校验。见 `windows/README.md`。
- 02～10 启动参数 `--user-data-dir=<数据目录>`，环境变量 `CLAUDE_CONFIG_DIR=<数据目录>/ClaudeCodeConfig`。
- 界面只显示本机已有的账号（数据目录存在、正在运行或正在启动），按编号紧凑排列；没有任何账号时显示空状态提示。“新建实例”创建 01～10 中编号最小的未使用账号。编号与目录映射不变。
- 已有目录直接复用，不迁移、删除、覆盖、修改权限。新目录仅本人可访问。
  - **[Windows 差异，已确认]** 不设置 ACL，新目录继承 `%APPDATA%` 默认权限（本人、SYSTEM、Administrators），与“仅本人可访问”意图一致。
- 01 的子进程环境不得带 `CLAUDE_CONFIG_DIR`。Windows 用 `UseShellExecute=false` 会继承启动器环境，因此须显式移除；macOS 经 LaunchServices 启动，不继承启动器环境，只附加显式设置的变量，无需处理。

## 进程识别

- 只认官方主程序路径；排除 Helper、GPU、renderer（参数含 `--type`）以及 Claude Code CLI。
- 从系统读取命令行参数解析 `--user-data-dir`（支持 `=` 与空格两种形式）：无参数为 01；重复参数、相对路径或缺值视为无法识别并暂停操作；不在 01～10 内的目录记为未管理实例。
- 不读取进程环境变量（可能含凭据）。
- 任何官方实例无法核对参数时，暂停启动，不重复启动、不结束进程。

## 启动与激活

- 运行中：按 PID 激活。激活前再次核对该 PID 的路径和账号；不以按程序名打开代替。
  - **[Windows 例外，已确认]** 主进程在托盘中、按 PID 找不到可见窗口时，允许用同一 `--user-data-dir` 再启动一次，由 Claude 单实例锁（4.29 含 `requestSingleInstanceLock`，按 userData 区分）转交已有进程显示窗口。必须同时满足：该账号恰好一个已核实主进程且无可见窗口；使用该 PID 的可执行文件；之后重新扫描，若同账号出现第二个主进程则报错，不结束任何进程。macOS 保持纯 PID 激活，最小化窗口经辅助功能恢复。
- 同一账号多个主进程：报错，不激活、不结束任何实例。
- 未运行：校验存储后以新实例启动；启动中禁用按钮防重入，35 秒未出现主进程报失败。
- 启动器单实例；再次打开时唤起已有窗口。关闭启动器不退出 Claude。

## 存储校验

只检查元数据，从不读取内容。目录及各级上级不得为链接或非目录；以下存储项不得为链接、非本人所有、普通文件硬链接：
`Cookies`、`Cookies-journal`、`Network`、`Local Storage`、`Session Storage`、`IndexedDB`、`Partitions`、`Local State`、`Preferences`、`config.json`、`Cache`、`Code Cache`、`Service Worker`、`ClaudeCodeConfig`。

Windows 对应：重解析点（符号链接、junction）等同链接；硬链接数通过文件信息查询。所有者按 ACL 所有者 SID 判断，以管理员身份创建的文件属于 Administrators 组，会被拒绝。

## 禁止事项

- 不修改、复制、重签官方 Claude 程序（仅可复制图标）。
- 不读取、复制、打印凭据；登录由用户完成。
- 不强杀 Claude；不修改系统权限或安全设置。
- 不伪装设备指纹或网络出口。
- 删除账号功能暂停。
  - **[Windows 兼容例外，已确认]** Windows 1.2.0 已发布回收站删除。开发版隐藏删除入口，只对此前未完成的删除保留重试（否则该账号无法启动）。这是遗留状态的退出路径，与 macOS 保留旧版“结束登录保护”同理；不恢复或扩展删除功能。

## 自检

每端提供 `--self-test`：账号映射、Code 配置映射唯一性、参数解析（含 Helper/CLI 排除、异常参数）、路径校验（链接、硬链接拒绝），只用随机合成目录并清理。

Windows 端的 `ClaudeLauncher.exe` 是 GUI 程序，需用 `ClaudeLauncher.exe --self-test | Write-Output` 运行，才会等待退出并显示结果。
