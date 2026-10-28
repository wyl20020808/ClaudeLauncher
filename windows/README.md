# Windows 版

按 [跨平台行为规格](../docs/SPEC.md) 原生实现，不与 macOS 共享代码。由此前独立维护的 Windows 启动器（发布版 1.2.0）整合而来，2026-10-28 对齐 macOS 1.6（feature/windows-sync）。本目录是开发版，未发布，也未替换客户包。

## 技术栈

C# 5 / .NET Framework 4.8 WPF（系统自带），无 NuGet 依赖。构建脚本直接调用系统 `csc.exe`，日常运行与构建都不需要 Python、Node.js 或额外运行时。`scripts/*.py` 和 `updates/server.py` 只供维护者打包和运行自建更新服务。安装包用 Inno Setup 6 生成（仅打包时需要）。

| 功能 | 实现 |
|------|------|
| 枚举进程、主程序路径、命令行、启动时间 | WMI `Win32_Process`（只取 PID、ExecutablePath、CommandLine、CreationDate，限当前会话） |
| 参数解析 | `CommandLineToArgvW` |
| 按 PID 激活 | `EnumWindows` + `GetWindowThreadProcessId`，`ShowWindow` + `SetForegroundWindow` |
| 启动实例 | `ProcessStartInfo`（`UseShellExecute=false`，设置 `CLAUDE_CONFIG_DIR`），返回新进程 PID |
| 单实例 | 按用户 SID 命名的 Mutex，重复打开时激活已有窗口 |
| 存储校验 | `FileAttributes.ReparsePoint`、ACL 所有者、`GetFileInformationByHandle` 链接数（只读属性，不打开内容），存储项内部递归，在后台线程执行 |
| 界面 | WPF 代码构建，水墨浅色四页（实例管理、账号管理、日志记录、系统设置） |

目录：`src/` 生产源码（`ClaudeLauncher.cs` 核心逻辑，`LauncherUI.cs` 界面与流程，`Storage.cs` 存储校验与自检），`tests/` 自动检查，`scripts/` 构建、安装包与发布工具，`installer/` Inno Setup 脚本，`assets/` 图标与更新验签公钥，`updates/` 自建更新服务，`docs/` 开发、设计和用户说明。界面插画取自仓库根目录 `resources/ui/*.webp`，构建时转为 JPEG 内嵌，客户电脑不需要 WebP 解码器。

## 构建与自检

在 `windows/` 目录下运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

该脚本编译 `artifacts/bin/ClaudeLauncher.exe` 和 `LauncherTests.exe`，先跑自动检查，再跑内置自检。加 `-Preview`（或运行 `scripts/preview-ui.ps1`）还会离屏渲染四个页面到 `artifacts/previews/`。单独运行自检（GUI 程序需经管道才会等待退出并输出结果）：

```powershell
artifacts\bin\ClaudeLauncher.exe --self-test | Write-Output
```

自检只使用 `%TEMP%\claude-selftest-*` 随机目录，结束后清理。覆盖范围：

- 账号映射，Code 配置目录映射唯一；
- 参数解析：`=` 和空格两种形式；排除 Helper/GPU/renderer；按路径排除 Claude Code CLI；拒绝重复、相对路径和缺值；
- 01 不传参数，也不继承 `CLAUDE_CONFIG_DIR`；02 正确设置 `CLAUDE_CONFIG_DIR`；
- 存储校验：账号目录本身、顶层存储项、存储项内部深层的硬链接或 junction（含 `ClaudeCodeConfig` 内部）都拒绝；非递归复核只看顶层；
- 同账号主进程唯一性；托盘重启动使用该 PID 自身的程序；
- 只显示已有账号、新建取最小未用编号；
- 保守 Code 状态（默认 / 待启动 / 未确认 / 已指定，含 PID 变化、早于请求、重复主进程）；
- 删除准入（01、运行中、启动或检查中、登录保护中均拒绝）；
- 登录保护准入（其他账号、未管理实例、目标重复主进程、其他账号进行中均拒绝）、保护期间只能打开目标、持久化与清除；
- 检查期间原 PID 退出或被替换时不自动重启、不切换。

更新安装逻辑改动后，另运行 `scripts/test-install.ps1`。

## 安装包（Setup）

更新与安装的完整规则（来源回退、清单签名、先卸载再安装、回滚）见 [SPEC 更新与安装](../docs/SPEC.md#更新与安装)，以此为准。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1
```

先执行 `build.ps1`（含自动检查与自检），再调用 Inno Setup 6（`ISCC.exe`，可用 `winget install JRSoftware.InnoSetup --scope user` 安装）生成 `artifacts/setup/ClaudeLauncherSetup-1.6.0.exe`（SPEC 文件名）。产物不提交到 Git。

- 按当前用户安装到 `%LOCALAPPDATA%\Programs\Claude 多开启动器`，不需要管理员权限；创建开始菜单快捷方式，桌面快捷方式可选；“应用和功能”中可卸载。
- 先卸载再安装（SPEC）：用固定 AppId 查当前用户的卸载项；已安装时提示“检测到已安装 x.y.z，将先卸载再安装新版”。随后按 PID 核对、对旧安装目录中运行的启动器窗口发送正常关闭（WM_CLOSE，不结束进程，不涉及 Claude），等待退出；再静默运行旧版卸载程序，等卸载项和旧卸载程序都消失后才安装新版。启动器无法正常关闭（如删除未完成）或旧版卸载失败、超时，都中止安装并提示原因。
- 卸载只删除安装的 `ClaudeLauncher.exe`、卸载程序以及自建更新在安装目录内留下的 `.previous-*`、`.new-*`、`.failed-*` 文件。**不删除任何账号数据目录**（`%APPDATA%\Claude-*`、`%LOCALAPPDATA%\Claude-Data`、商店版 `Packages\Claude_*`），也不删除启动器日志、登录保护和删除状态目录 `%LOCALAPPDATA%\Claude-MultiLauncher`。
- 升级覆盖安装时，Inno Setup 只会请求关闭占用本程序文件的进程（启动器本身），不涉及 Claude。
- 版本号与 macOS 一致：1.6.0（文件版本 1.6.0.0，见 `src/Updates.cs`；界面显示 V1.6）。
- 安装向导使用 Inno Setup 默认英文界面（Inno 6.7.3 未内置简体中文向导文件），程序名、快捷方式为中文。
- **代码签名：未签名。** 本机没有代码签名证书，安装程序和 `ClaudeLauncher.exe` 都没有 Authenticode 签名。客户首次运行会看到 SmartScreen“Windows 已保护你的电脑”，需点“更多信息 → 仍要运行”；部分杀毒软件可能更谨慎。需要正式证书（OV/EV）后在打包时对 exe 和 setup.exe 签名，此项待用户决定。
- 与自建更新（`Updates.cs`）的关系：**当前代码**仍是 1.2.0 协议——检查自建服务的 `latest.json`（RSA 签名），下载单个 `ClaudeLauncher-x.x.x.x.exe` 并在安装目录内原地替换（用户目录可写，无需管理员）；原地更新后“应用和功能”里的版本号停留在安装时的版本。SPEC“更新与安装”已改为：`latest-windows.json`（RSA-3072 + SHA-256，两端同一公钥）、自建失败回退 GitHub Releases、两处都失败时提示前往 GitHub 下载，自动更新下载并静默运行 Setup。这部分 **Windows 待同步**，本轮未改动更新逻辑（需要新密钥与发布流程，见 STATUS）。

## 与 SPEC 的对应

- 01 不传覆盖参数，并从子进程环境中移除 `CLAUDE_CONFIG_DIR`；02 为 `%APPDATA%\Claude-Second`；03～10 为 `%APPDATA%\Claude-Profile-NN`。02～10 传 `--user-data-dir`，并设置 `CLAUDE_CONFIG_DIR=<数据目录>\ClaudeCodeConfig`（不存在时创建）。
- 只认官方安装路径：MSIX `WindowsApps\Claude_*\app`、`AnthropicClaude`、`Programs\Claude`、`Program Files`。参数含 `--type=` 的进程排除；Claude Code CLI 虽然也叫 `claude.exe`，按路径排除。未管理目录的官方主进程计入扫描（用于登录保护准入），但不显示为账号。
- `--user-data-dir` 重复、缺值或为相对路径时拒绝识别。任一官方进程无法读取命令行时整体暂停，不启动、不结束进程。不读取进程环境变量。
- 同一账号存在多个主进程时报错，不激活、不结束。
- 界面只显示本机已有账号（任一候选目录存在、运行中、启动中或检查中），紧凑排列；无账号时显示空状态；“新建实例”取 01～10 中最小的未使用编号。
- 打开账号：先在后台递归检查存储（卡片显示“检查中”，同账号禁止重复打开/删除），完成后重新扫描，核对原 PID、登录保护状态；原 PID 退出或被替换时报错，不自动重启、不切换 PID。启动前再快速复查目录链与顶层存储项。启动中禁用按钮，35 秒内未出现主进程窗口则报告。
- 新建账号弹出提醒：直接创建（其他 Claude 保持运行）/ 进入登录保护 / 取消，不强制保护。
- “···”菜单：在资源管理器中显示目录、复制目录路径、登录 / 重新登录保护、删除该实例（或“重试删除”）。
- 删除：01 不可删；运行中、启动中、检查中、登录保护中不可删。二次确认后重新扫描、校验路径，把该账号所有候选目录（含 MSIX 虚拟化目录）移到回收站；删除前任一会话中该目录的 Claude 进程（含辅助进程）都会阻止。保留“删除未完成”的重试。
- Code 标签：01“Code 默认”；未运行“Code 待启动”；只有启动器本次 `Process.Start` 返回的 PID、扫描到该账号唯一主进程且启动时间不早于请求时间时显示“Code 已指定”；外部或旧进程、PID 变化、重复主进程均为“Code 未确认”。记录只在内存中，不读取进程环境变量。
- 登录保护：准入前、确认后各扫描一次；其他账号或未管理主进程、目标重复主进程、其他账号检查/启动中都阻止。保护账号保存在 `%LOCALAPPDATA%\Claude-MultiLauncher\login-protection`，重开保留。保护期间只能打开目标账号（仍要求其他 Claude 已退出），不能新建实例、不能删除受保护账号；结束保护需用户确认，有检查或启动操作时暂不允许结束。不自动退出、登出或结束任何实例。
- 不强杀 Claude。

## Windows 实测（2026-10-28，本机 Windows 11，Claude MSIX 4.29.0.0）

以下均为只读检查，未启动新的 Claude 实例，未触碰登录数据：

- 安装位置：`C:\Program Files\WindowsApps\Claude_4.29.0.0_x64__pzs8sxrjxfjjc\app\claude.exe`（文件名小写）；执行别名 `%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe` 存在。
- 01 主进程命令行没有参数，而它的 Helper 带 `--user-data-dir=%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc`。也就是说，MSIX 版默认 userData 在 `%LOCALAPPDATA%`，不在 `%APPDATA%`。
- 已有的 02、03 数据位于 `%APPDATA%\Claude-Second`、`%APPDATA%\Claude-Profile-03`；`Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\` 下也有同名的虚拟化目录。
- Helper 均带 `--type=`（gpu-process、renderer、utility、crashpad-handler）；crashpad 的 `--user-data-dir` 值不带引号。
- Claude Code CLI 位于 `%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc\claude-code\<版本>\<哈希>\claude.exe`，与桌面版同名。
- 只读检索 4.29.0 的 `app.asar`：包含 `process.env.CLAUDE_CONFIG_DIR`（与 macOS 1.6 的依据一致），也包含 `requestSingleInstanceLock` / `second-instance`。

## 与 SPEC 的差异

1. **01 数据目录**：MSIX 版为 `%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc`，旧安装可能是 `%APPDATA%\Claude`。02～10 的基础目录为 `%APPDATA%`，沿用已有数据。商店版可能把目录虚拟化到 `Packages\…\LocalCache\Roaming\`，识别、校验、显示和删除都同时覆盖这两处。（SPEC 已确认）
2. **托盘中没有可见窗口的实例**：仅当该账号恰好一个已核实主进程且无可见窗口时，用**该 PID 自身的可执行文件**、同一 `--user-data-dir` 再启动一次，由 Claude 单实例锁转交给已有进程显示窗口。5 秒后重新扫描，若同账号出现第二个主进程则报错，不结束任何进程。（SPEC 已确认）
3. **新目录权限**：不修改 ACL，继承 `%APPDATA%` 的默认权限（本人、SYSTEM、Administrators），不等同于 macOS 的 0700。（SPEC 已确认）
4. **所有者校验**：以管理员身份创建的文件，所有者是 Administrators 组，会被拒绝（失败即停止）。（SPEC 已确认）
5. **进程范围**：只扫描当前登录会话中的进程。（SPEC 已确认）
6. **额外功能**：签名云更新、本地诊断日志（设置页“本地诊断日志 → 打开”）。
7. **启动请求超时**：`Process.Start` 是同步调用，立即返回 PID 或失败，不存在 macOS LaunchServices“请求超过 35 秒仍未回调”的悬挂状态；35 秒超时只用于等待主进程窗口出现。
8. **Code 已指定的依据**：用 `Process.Start` 返回的 PID 与 WMI `CreationDate`（不早于请求时间）认定“本次启动的新进程”，对应 macOS 的 `NSRunningApplication` 与 `launchDate`。若官方程序启动时转交给其他进程，PID 不匹配，保守显示“Code 未确认”。
9. **1.2.0 遗留的 01 删除标记**：01 不可删，因此 1.2.0 中对 01 发起且未完成的删除无法在启动器内重试，01 会一直提示“删除未完成”。需用户手动处理该标记（`%LOCALAPPDATA%\Claude-MultiLauncher\pending-01`），启动器不自动清除。

## 验证记录

### 自检与自动检查（2026-10-28，feature/windows-sync，已执行）

- `scripts/build.ps1 -Preview`：编译通过。`LauncherTests` 241 项断言通过（含四个页面以合成状态布局、删除确认框默认取消、启动探针验证参数和 `CLAUDE_CONFIG_DIR`）。
- `ClaudeLauncher.exe --self-test | Write-Output`：98 项通过，临时目录已清理。
- 离屏渲染四个页面（合成进程状态，不扫描或启动 Claude），人工比对 `docs/assets/界面.png` 与 macOS 四页：布局、插画、文字无裁切。图片在 `artifacts/previews/`，未提交。离屏渲染不代表桌面输入。
- `scripts/setup.ps1`：生成 `ClaudeLauncherSetup-1.6.0.exe`（约 2.9 MB）。静默安装到 `%TEMP%` 随机目录（`/NOICONS`，不运行程序）：文件版本 1.6.0.0；放入一个模拟的 `.previous-*` 更新备份后静默卸载：安装目录被完整移除，`%APPDATA%\Claude-Second`、`Claude-Profile-03`、`%LOCALAPPDATA%\Claude-Data`、`Claude-MultiLauncher` 均保留，卸载注册表项已清除。随后测试覆盖安装：先装到随机目录并放入模拟 `.previous-*` 备份，再静默运行同一 Setup——旧版先被静默卸载（备份被移除），新版装回同一目录，卸载程序仍为 `unins000`；最后卸载，目录与注册表项清除，账号目录保留。启动器运行中时的关闭流程未测（未启动启动器）。
- `scripts/test-install.ps1`：见 STATUS 本轮记录。

### GUI / 真实实例（本轮未执行）

- 实际窗口中的点击、页面切换、“···”菜单、新建提醒三选项、登录保护进入/结束/重开保留、检查中连点、删除确认与回收站、导出/清空日志、检查更新：**未验证**。
- PID 激活、托盘恢复（用 PID 自身程序重启动、5 秒复查）：**未验证**。
- 真实 Claude 4.29 实例的递归存储检查耗时（Cache 文件较多时）、“Code 已指定”是否在真实 MSIX 启动时出现：**未验证**。
- Code 标签页是否实际使用 `CLAUDE_CONFIG_DIR`、双账号登录隔离、登录回调归属：**未验证**。
- 安装包：开始菜单与桌面快捷方式、交互式向导、覆盖升级、从安装目录运行后的自建更新、SmartScreen 实际提示：**未验证**。
