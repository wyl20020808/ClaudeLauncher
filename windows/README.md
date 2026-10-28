# Windows 版

按 [跨平台行为规格](../docs/SPEC.md) 原生实现，不与 macOS 共享代码。由此前独立维护的 Windows 启动器（发布版 1.2.0）整合而来；本目录是开发版，未发布，也未替换客户包。

## 技术栈

C# 5 / .NET Framework 4.8 WPF（系统自带），无 NuGet 依赖。构建脚本直接调用系统 `csc.exe`，日常运行与构建都不需要 Python、Node.js 或额外运行时。`scripts/*.py` 和 `updates/server.py` 只供维护者打包和运行自建更新服务。

| 功能 | 实现 |
|------|------|
| 枚举进程、主程序路径、命令行 | WMI `Win32_Process`（只取 PID、ExecutablePath、CommandLine，限当前会话） |
| 参数解析 | `CommandLineToArgvW` |
| 按 PID 激活 | `EnumWindows` + `GetWindowThreadProcessId`，`ShowWindow` + `SetForegroundWindow` |
| 启动实例 | `ProcessStartInfo`（`UseShellExecute=false`，设置 `CLAUDE_CONFIG_DIR`） |
| 单实例 | 按用户 SID 命名的 Mutex，重复打开时激活已有窗口 |
| 存储校验 | `FileAttributes.ReparsePoint`、ACL 所有者、`GetFileInformationByHandle` 链接数（只读属性，不打开内容） |

目录：`src/` 生产源码，`tests/` 自动检查，`scripts/` 构建与发布工具，`assets/` 官方图标与更新验签公钥，`updates/` 自建更新服务，`docs/` 开发、设计和用户说明。

## 待办：Setup 安装程序

与 macOS DMG 对应，Windows 需要提供一个 Setup 安装程序，方便发给客户：

- 推荐 Inno Setup（单个 setup.exe，体积小）；也可用 WiX 生成 MSI。
- 安装到 `%LOCALAPPDATA%\Programs\Claude 多开启动器` 或 `Program Files`，创建开始菜单与可选桌面快捷方式，提供卸载入口。
- 卸载只删除启动器程序，**不得删除任何账号数据目录**（`%APPDATA%\Claude-*`、`Claude-Data` 等）。
- 对安装程序和 exe 做代码签名（无签名时客户会看到 SmartScreen 警告）。
- 版本号与 macOS 保持一致，输出到 `windows/artifacts/`，不提交到 Git。
- 与现有自建更新（Updates.cs）的关系需说明：安装版是否仍走自动更新、更新包是否也用 Setup。

## 构建与自检

在 `windows/` 目录下运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

该脚本编译 `artifacts/bin/ClaudeLauncher.exe` 和 `LauncherTests.exe`，先跑自动检查，再跑内置自检。单独运行自检（GUI 程序需经管道才会等待退出并输出结果）：

```powershell
artifacts\bin\ClaudeLauncher.exe --self-test | Write-Output
```

自检只使用 `%TEMP%\claude-selftest-*` 随机目录，结束后清理。覆盖范围：

- 账号映射，Code 配置目录映射唯一；
- 参数解析：`=` 和空格两种形式；排除 Helper/GPU/renderer；按路径排除 Claude Code CLI；拒绝重复、相对路径和缺值；
- 01 不传参数，也不继承 `CLAUDE_CONFIG_DIR`；02 正确设置 `CLAUDE_CONFIG_DIR`；
- 存储项和账号目录本身为硬链接或 junction 时拒绝。

更新安装逻辑改动后，另运行 `scripts/test-install.ps1`。

## 与 SPEC 的对应

- 01 不传覆盖参数，并从子进程环境中移除 `CLAUDE_CONFIG_DIR`；02 为 `%APPDATA%\Claude-Second`；03～10 为 `%APPDATA%\Claude-Profile-NN`。02～10 传 `--user-data-dir`，并设置 `CLAUDE_CONFIG_DIR=<数据目录>\ClaudeCodeConfig`（不存在时创建）。
- 只认官方安装路径：MSIX `WindowsApps\Claude_*\app`、`AnthropicClaude`、`Programs\Claude`、`Program Files`。参数含 `--type=` 的进程排除；Claude Code CLI 虽然也叫 `claude.exe`，按路径排除。
- `--user-data-dir` 重复、缺值或为相对路径时拒绝识别。任一官方进程无法读取命令行时整体暂停，不启动、不结束进程。不读取进程环境变量。
- 同一账号存在多个主进程时报错，不激活、不结束。启动中禁用按钮；35 秒内未出现主进程则报告失败。
- 启动前校验 SPEC 列出的存储项及目录各级上级（含 MSIX 虚拟化目录）。
- 不强杀 Claude。删除入口按 SPEC 暂停（见差异 6）。

## Windows 实测（2026-10-28，本机 Windows 11，Claude MSIX 4.29.0.0）

以下均为只读检查，未启动新的 Claude 实例，未触碰登录数据：

- 安装位置：`C:\Program Files\WindowsApps\Claude_4.29.0.0_x64__pzs8sxrjxfjjc\app\claude.exe`（文件名小写）；执行别名 `%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe` 存在。
- 01 主进程命令行没有参数，而它的 Helper 带 `--user-data-dir=%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc`。也就是说，MSIX 版默认 userData 在 `%LOCALAPPDATA%`，不在 `%APPDATA%`。
- 已有的 02、03 数据位于 `%APPDATA%\Claude-Second`、`%APPDATA%\Claude-Profile-03`；`Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\` 下也有同名的虚拟化目录。
- Helper 均带 `--type=`（gpu-process、renderer、utility、crashpad-handler）；crashpad 的 `--user-data-dir` 值不带引号。
- Claude Code CLI 位于 `%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc\claude-code\<版本>\<哈希>\claude.exe`，与桌面版同名。
- 只读检索 4.29.0 的 `app.asar`：包含 `process.env.CLAUDE_CONFIG_DIR`（与 macOS 1.6 的依据一致），也包含 `requestSingleInstanceLock` / `second-instance`。

## 与 SPEC 的差异（SPEC 已定稿确认）

1. **01 数据目录**：MSIX 版为 `%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc`，旧安装可能是 `%APPDATA%\Claude`。02～10 的基础目录为 `%APPDATA%`，沿用已有数据。商店版可能把目录虚拟化到 `Packages\…\LocalCache\Roaming\`，识别和校验都同时覆盖这两处。
2. **托盘中没有可见窗口的实例（SPEC 已确认的例外）**：仅当该账号恰好一个已核实主进程且无可见窗口时，用**该 PID 自身的可执行文件**、同一 `--user-data-dir` 再启动一次，由 Claude 单实例锁转交给已有进程显示窗口。5 秒后重新扫描，若同账号出现第二个主进程则报错，不结束任何进程。
3. **新目录权限**：不修改 ACL，继承 `%APPDATA%` 的默认权限（本人、SYSTEM、Administrators），不等同于 macOS 的 0700。
4. **所有者校验**：以管理员身份创建的文件，所有者是 Administrators 组，会被拒绝（失败即停止）。
5. **进程范围**：只扫描当前登录会话中的进程。
6. **额外功能**：签名云更新、本地诊断日志、账号删除（1.2.0 已发布）。删除入口现按 SPEC 隐藏，只对此前未完成的删除保留重试，避免账号卡在“删除未完成”状态。

## 验证记录

### 自检与自动检查（2026-10-28，已执行）

- `scripts/build.ps1`：编译通过。`LauncherTests` 241 项断言通过，包括：用启动探针验证参数和 `CLAUDE_CONFIG_DIR` 确实传入子进程、01 不继承该变量、WPF 布局离屏加载。
- `ClaudeLauncher.exe --self-test`：60 项通过，临时目录已清理。
- 托盘重启动适配后复跑：`LauncherTests` 241 项、`--self-test` 63 项通过（新增同账号主进程唯一性、使用该 PID 自身程序的检查），临时目录已清理。
- `scripts/test-install.ps1`：本轮未改更新逻辑，**未运行**。

### GUI / 真实实例（本轮未执行）

- 启动器界面点击、PID 激活、托盘恢复（含用 PID 自身程序重启动、5 秒后复查第二个主进程的报错）：**未验证**。本轮未改界面布局；1.2.0 的历史验收见原项目记录。
- Claude 4.29 实例是否接受 `--user-data-dir`、Code 标签页是否使用 `CLAUDE_CONFIG_DIR`、MSIX 虚拟化对 `ClaudeCodeConfig` 位置的影响：**未验证**，需要重启或新建账号实例，未执行。
- 删除入口隐藏后的界面：**未验证**。
- 双账号登录隔离、登录回调归属：**未验证**。
