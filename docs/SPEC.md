# 跨平台行为规格

macOS（`src/main.m`）和 Windows（`windows/`）各自原生实现，共同遵守本规格。macOS 1.6 是参考实现；规格变化时两端同步修改并在 STATUS 记录。

## 账号与目录

| 编号 | 数据目录 | Claude Code 配置 |
|------|----------|------------------|
| 01 | 官方默认目录，不传覆盖参数 | 官方默认（`~/.claude`） |
| 02 | `<基础目录>/Claude-Second` | `<数据目录>/ClaudeCodeConfig` |
| 03～10 | `<基础目录>/Claude-Profile-NN` | `<数据目录>/ClaudeCodeConfig` |

- 基础目录：macOS 为 `~/Library/Application Support`；Windows 待确认官方 userData 位置（预期 `%APPDATA%`）。
- 02～10 启动参数 `--user-data-dir=<数据目录>`，环境变量 `CLAUDE_CONFIG_DIR=<数据目录>/ClaudeCodeConfig`。
- 已有目录直接复用，不迁移、删除、覆盖、修改权限。新目录仅本人可访问。

## 进程识别

- 只认官方主程序路径；排除 Helper、GPU、renderer（参数含 `--type`）以及 Claude Code CLI。
- 从系统读取命令行参数解析 `--user-data-dir`（支持 `=` 与空格两种形式）：无参数为 01；重复参数、相对路径或缺值视为无法识别并暂停操作；不在 01～10 内的目录记为未管理实例。
- 不读取进程环境变量（可能含凭据）。
- 任何官方实例无法核对参数时，暂停启动，不重复启动、不结束进程。

## 启动与激活

- 运行中：按 PID 激活。激活前再次核对该 PID 的路径和账号；不以按程序名打开代替。
- 同一账号多个主进程：报错，不激活、不结束任何实例。
- 未运行：校验存储后以新实例启动；启动中禁用按钮防重入，35 秒未出现主进程报失败。
- 启动器单实例；再次打开时唤起已有窗口。关闭启动器不退出 Claude。

## 存储校验

只检查元数据，从不读取内容。目录及各级上级不得为链接或非目录；以下存储项不得为链接、非本人所有、普通文件硬链接：
`Cookies`、`Cookies-journal`、`Network`、`Local Storage`、`Session Storage`、`IndexedDB`、`Partitions`、`Local State`、`Preferences`、`config.json`、`Cache`、`Code Cache`、`Service Worker`、`ClaudeCodeConfig`。

Windows 对应：重解析点（符号链接、junction）等同链接；硬链接数通过文件信息查询。

## 禁止事项

- 不修改、复制、重签官方 Claude 程序（仅可复制图标）。
- 不读取、复制、打印凭据；登录由用户完成。
- 不强杀 Claude；不修改系统权限或安全设置。
- 不伪装设备指纹或网络出口。
- 删除账号功能暂停。

## 自检

每端提供 `--self-test`：账号映射、Code 配置映射唯一性、参数解析（含 Helper/CLI 排除、异常参数）、路径校验（链接、硬链接拒绝），只用随机合成目录并清理。
