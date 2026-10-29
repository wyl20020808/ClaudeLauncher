# Windows 版

按 [跨平台行为规格](../docs/SPEC.md) 原生实现，不与 macOS 共享代码。由此前独立维护的 Windows 启动器（发布版 1.2.0）整合而来，2026-10-28 对齐 macOS 1.6，随后升级到 1.7.0 并实现新的更新协议（feature/windows-sync）。本目录是开发版，未发布，也未替换客户包。

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
| 界面 | WPF 代码构建，水墨浅色四页（实例管理、账号管理、日志记录、系统设置六分类） |
| 设置 | `%LOCALAPPDATA%\Claude-MultiLauncher\settings.json`（`src/Settings.cs`）；开机启动为 HKCU Run 的 `ClaudeMultiLauncher` 值 |

目录：`src/` 生产源码（`ClaudeLauncher.cs` 核心逻辑，`LauncherUI.cs` 界面与流程，`Storage.cs` 存储校验与自检），`tests/` 自动检查，`scripts/` 构建、安装包与发布工具，`installer/` Inno Setup 脚本，`assets/` 图标与更新验签公钥，`updates/` 自建更新服务，`docs/` 开发、设计和用户说明。界面插画取自仓库根目录 `resources/ui/*.webp`，构建时转为 JPEG 内嵌，客户电脑不需要 WebP 解码器。

## 构建与自检

在 `windows/` 目录下运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

该脚本编译 `artifacts/bin/ClaudeLauncher.exe` 和 `LauncherTests.exe`，先跑自动检查，再跑内置自检。加 `-Preview`（或运行 `scripts/preview-ui.ps1`）还会离屏渲染实例、账号、日志三页及系统设置六个分类到 `artifacts/previews/`。单独运行自检（GUI 程序需经管道才会等待退出并输出结果）：

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
- 检查期间原 PID 退出或被替换时不自动重启、不切换；
- 更新协议（一次性 RSA-3072 测试密钥）：`latest-windows.json` 验签与签名原文格式、篡改哈希/大小拒绝、其他密钥拒绝、平台/文件名/版本格式/大小上限/哈希格式/minimumOS 不符拒绝（即使签名正确）、非 Base64 签名与超大清单拒绝、`latest-macos.json` 验签；版本比较只升不降；系统版本比较；Authenticode 签名者规则与未签名文件识别；内置公钥为 RSA-3072。
- 网络边界：`UrlAllowed` 允许本项目清单／Setup 首次请求、同仓库标签下载路径及两个 GitHub 附件主机作跳转；拒绝 HTTP、非 443、用户信息、其他仓库、任意路径、查询串、其他清单、相似域名、自建服务、claude.ai、anthropic.com 等（每个地址首次与跳转两种情形）；未开启更新时检查与下载在创建任何请求前拒绝（请求计数不变）。设置文件默认关闭、保存往返、覆盖不残留临时文件、损坏／未知版本／类型不符回落关闭。
- 系统设置：11 项默认值与 SPEC 一致；单项异常（越界、类型不符、路径当页面名）只回落该项；全部字段往返；导出为 version 1 且不含账号数据；上次页面白名单；开机启动只接受正式安装路径、值比较规则（不读写真实注册表）。
- 账号别名与搜索：≤24 字、去首尾空白、拒绝控制字符；往返；无效条目跳过；损坏／未知版本报错且保留原文件；越界编号拒绝；别名、“账号 NN”、编号、目录均可匹配。
- 账号页与日志页（LauncherTests）：按编号与别名搜索、状态筛选、全选当前结果、部分选中中间态、隐藏选择清除、复制路径只含可见选择；别名写入启动器自己的文件、卡片与表格同步、无效别名不改动、留空恢复默认、不进入设置导出；日志分页（10 条、上／下一页启停）、搜索回到第一页、级别／实例／时间筛选、空结果说明、导出为全部筛选结果且按时间正序、内容只读可选并有全文提示。
- 界面（LauncherTests，合成设置 + 临时路径）：六个分类按钮、假选项已移除；开关改变草稿、切换分类保留草稿、撤销不写文件；恢复上次页面时默认页面禁用；保存后日志上限截断内存日志、插画隐藏、窗口内提示关闭；恢复默认只填草稿；更新检查在未开启或有未保存设置时不创建请求；诊断只含版本、系统版本与联网开关。

1.2.0 兼容的 `--apply-update` 原地替换改动后，另运行 `scripts/test-install.ps1`。

## 安装包（Setup）

更新与安装的完整规则（来源回退、清单签名、先卸载再安装、回滚）见 [SPEC 更新与安装](../docs/SPEC.md#更新与安装)，以此为准。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1
```

先执行 `build.ps1`（含自动检查与自检），再调用 Inno Setup 6（`ISCC.exe`，可用 `winget install JRSoftware.InnoSetup --scope user` 安装）生成 `artifacts/setup/ClaudeLauncherSetup-1.7.0.exe`（SPEC 文件名）。要求 Windows 10.0.19041 及以上（与清单 `minimumOS` 一致）。产物不提交到 Git。

- 按当前用户安装到 `%LOCALAPPDATA%\Programs\Claude 多开启动器`，不需要管理员权限；创建开始菜单快捷方式，桌面快捷方式可选；“应用和功能”中可卸载。
- 先卸载再安装（SPEC）：用固定 AppId 查当前用户的卸载项；已安装时提示“检测到已安装 x.y.z，将先卸载再安装新版”。随后按 PID 核对、对旧安装目录中运行的启动器窗口发送正常关闭（WM_CLOSE，不结束进程，不涉及 Claude），等待退出；再静默运行旧版卸载程序，等卸载项和旧卸载程序都消失后才安装新版。启动器无法正常关闭（如删除未完成）或旧版卸载失败、超时，都中止安装并提示原因。
- 卸载只删除安装的 `ClaudeLauncher.exe`、卸载程序以及自建更新在安装目录内留下的 `.previous-*`、`.new-*`、`.failed-*` 文件。**不删除任何账号数据目录**（`%APPDATA%\Claude-*`、`%LOCALAPPDATA%\Claude-Data`、商店版 `Packages\Claude_*`），也不删除启动器日志、登录保护和删除状态目录 `%LOCALAPPDATA%\Claude-MultiLauncher`。
- 升级覆盖安装时，Inno Setup 只会请求关闭占用本程序文件的进程（启动器本身），不涉及 Claude。
- 版本号 1.7.3（程序集 1.7.3.0，见 `src/Updates.cs`）；与 macOS 各自递增，不要求一致。
- 安装向导为简体中文：`installer/ChineseSimplified.isl` 取自 Inno Setup 官方源码 `Files/Languages/ChineseSimplified.isl`（6.5.0+；6.7.3 安装包未自带）。
- **代码签名：未签名。** 本机没有代码签名证书，安装程序和 `ClaudeLauncher.exe` 都没有 Authenticode 签名。客户首次运行会看到 SmartScreen“Windows 已保护你的电脑”，需点“更多信息 → 仍要运行”；部分杀毒软件可能更谨慎。需要正式证书（OV/EV）后在打包时对 exe 和 setup.exe 签名，此项待用户决定。
- 应用内更新运行 Setup 的参数 `/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1`：只显示中文进度窗口（“正在卸载旧版… / 正在安装”），不显示向导页和确认框；`/RELAUNCH=1` 仅在静默模式下让 Setup 装好后重新打开启动器。1.7.1 及更早版本用 `/VERYSILENT`，不显示任何窗口。

## 应用内更新（SPEC 更新与安装）

- 开关（feature/windows-update-boundary 起）：`allowUpdates` 默认关闭，保存在 `%LOCALAPPDATA%\Claude-MultiLauncher\settings.json`；系统设置“关于软件 → 允许检查 GitHub 更新”切换后需点“保存设置”。未开启时设置页“检查更新”与托盘“检查更新…”都不联网，只提示开启；有未保存设置时不开始；下载期间设置被改则安装前取消并删除临时文件。
- 时机：开启后每次打开启动器检查一次（只在有新版时提示，失败只记日志）+ 手动检查；没有定时或后台重复检查，已删除 8 秒／24 小时自动检查及 `last-update-check`。
- 来源：只用 `https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-windows.json` 与同 Release 的 Setup；失败时显示原因并提供“前往 GitHub 下载”（打开 Releases 页面）。自建服务与“取较高版本”已从客户端移除（发布脚本过渡期仍为旧客户端同步自建服务）。
- 清单：`platform=windows`、`version` x.y.z、`file=ClaudeLauncherSetup-<版本>.exe`、`size`（1 KB～50 MB）、小写 `sha256`、`minimumOS`、`signature`；签名原文为 platform、version、file、size、sha256 各一行（`\n` 结尾），RSA-3072 + SHA-256（PKCS#1 v1.5），公钥 `assets/update-public-key.xml`（与 macOS 同一把）。清单上限 16 KB。
- 网络：只走 HTTPS（TLS 1.2，证书校验保持系统默认）；所有地址经 `Updates.UrlAllowed` 在创建请求前校验：首次请求只允许上面的清单与严格版本文件名 Setup；不自动跳转，跳转只允许同仓库 `releases/download/<标签>/` 下的同类文件及 `objects.githubusercontent.com`、`release-assets.githubusercontent.com`；一律 443、无用户信息，最多 5 跳。请求不带凭据、不预认证、无 Cookie、不缓存；每次请求 15 秒超时，清单 30 秒、安装包 300 秒总时限，并按清单 `size` 限制下载字节数。
- 只升级不降级；系统低于 `minimumOS` 时只提示不安装。下载到 `%LOCALAPPDATA%\Claude-MultiLauncher\updates\<随机>\`，大小或 SHA-256 不符即删除。
- 运行前再次核对哈希，并比对 Authenticode 签名者：当前程序已签名时，Setup 必须由同一证书签名且签名有效。**当前程序与 Setup 都没有签名，无法比对签名者，只依赖清单签名与 SHA-256**（SPEC【Windows 差异】）。
- 进度窗口（1.7.2 起）：确认“立即更新”后显示“下载 → 校验 → 安装”三步；下载有真实进度（MB 与百分比、来源 GitHub），可取消（取消即删除临时文件，不做任何改动），关闭窗口视为取消；校验时为不定进度；校验通过后提示“启动器即将关闭…完成后会自动重新打开”，2 秒后启动器关闭，Setup 接着显示中文安装进度。不退出、不结束任何 Claude。
- 1.2.0 兼容：程序保留 `--apply-update`（旧清单 `latest.json` + 单个 exe 原地替换），供仍在 1.2.0 的客户端升级到 1.7.0 单 exe。

## 发布（发布机）

两种方式：只发布 Windows 时直接运行 `scripts/publish-release.ps1`（标签 `windows-v<版本>`，从当前最新 Release 带入已签名的 macOS 清单与 DMG，验签核对后一并放进新 Release，自建服务的 macOS 清单不动）；两端同时发布时运行 `scripts/publish-release.ps1 -MacSize <字节> -MacSha256 <哈希>`（标签 `v<版本>`）：构建 Setup → 从草稿 Release `v<版本>` 下载 macOS DMG 并核对 macOS 给出的 size/sha256 → 内存中解密 DPAPI 私钥，签名 `latest-windows.json`、`latest-macos.json`（首行 `macos`、`ClaudeLauncher-<版本>.dmg`、`minimumOS=12.0`）和 1.2.0 兼容 `latest.json`，并本地验签 → 安装包复制到 `updates/public/` → Setup 与两份清单上传到草稿 → 原子切换自建服务清单 → 草稿转正式 → 从自建服务与 GitHub 各下载一次清单和安装包验签核对。正式发布后的任何失败都会恢复自建清单并把 Release 改回草稿。`-DryRun` 只构建、签名和本地校验，不上传（可用测试密钥）。私钥不落盘、不输出。服务端白名单与维护步骤见 `updates/维护说明.md`。

## 常驻图标（通知区域）

- 启动器运行期间托盘常驻（可在“常规设置”关闭图标）；关闭主窗口只隐藏（首次弹一次提示），程序和图标继续运行；“关闭窗口后继续运行”关闭时，关闭窗口即退出启动器。左键单击或双击显示主窗口，右键弹出菜单。
- 菜单：显示启动器窗口 / 本机账号（“账号 NN · 运行中/未运行”，进行中时显示检查中、启动中、删除未完成；点击与卡片主按钮走同一流程：PID 核对、登录保护、存储检查）/ 检查更新… / 退出启动器（只退出启动器，不退出任何 Claude；删除进行中时不允许）。
- 每次打开菜单同步重新扫描进程；扫描失败时账号项禁用并显示原因。菜单不显示任何账号内容。
- 安装程序请求退出时发送专用消息 `ClaudeMultiLauncher.Exit`（WM_CLOSE 只会隐藏）；再次打开启动器时用命名事件让已运行实例显示窗口。
- 图标 `assets/app.ico` 由 `scripts/make-icon.ps1` 从 `resources/icon/icon_full_1024.png` 生成（16、20、24、32、40、48、64、256）；16～24 为简化图形。官方 `claude.ico` 与提取脚本已移除。

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
6. **额外功能**：本地诊断日志（设置页“本地诊断日志 → 打开”）；1.2.0 兼容的 `--apply-update`。
10. **更新签名者比对**：程序与 Setup 尚无 Authenticode 签名，当前无法执行“同一证书”检查，只依赖清单 RSA 签名与 SHA-256；签名后自动生效。
11. **更新回滚**：Setup 先卸载旧版再安装；新版安装失败时旧版已卸载，不能自动回滚（账号数据不受影响）。
12. **minimumOS 格式**：每段允许 1～5 位数字，以容纳 Windows 构建号（10.0.19041）。
7. **启动请求超时**：`Process.Start` 是同步调用，立即返回 PID 或失败，不存在 macOS LaunchServices“请求超过 35 秒仍未回调”的悬挂状态；35 秒超时只用于等待主进程窗口出现。
8. **Code 已指定的依据**：用 `Process.Start` 返回的 PID 与 WMI `CreationDate`（不早于请求时间）认定“本次启动的新进程”，对应 macOS 的 `NSRunningApplication` 与 `launchDate`。若官方程序启动时转交给其他进程，PID 不匹配，保守显示“Code 未确认”。
9. **1.2.0 遗留的 01 删除标记**：01 不可删，因此 1.2.0 中对 01 发起且未完成的删除无法在启动器内重试，01 会一直提示“删除未完成”。需用户手动处理该标记（`%LOCALAPPDATA%\Claude-MultiLauncher\pending-01`），启动器不自动清除。

## 验证记录

### 日志持久化与日志页改版（2026-10-29，feature/windows-log-persist，对应 macOS 1.7.11）

- 自检（已执行）：`build.ps1 -Preview` 通过，LauncherTests 302 项、`--self-test` 198 项。新增：日志文件与 macOS 同格式往返、坏记录与整体损坏处理、数字页码规则；界面中 6 个时间选项与 7 天默认、页码导航、汇总文字、写入文件、重启后恢复、“本次运行”只含新进程记录。日志页与设置页离屏图已人工查看；预览与测试的日志文件写在临时目录并清理。
- GUI（**未验证**）：桌面中的下拉弹出与键盘操作、真实重启／软件更新后日志保留、清空确认、导出对话框。

### 账号页与日志页交互（2026-10-29，feature/windows-accounts-logs）

- 自检（已执行）：`build.ps1 -Preview` 通过，LauncherTests 295 项（新增 26 项账号／日志检查）、`--self-test` 189 项（新增 10 项别名检查）。账号页与日志页离屏图已人工查看，搜索框、筛选、复选框、铅笔按钮、分页无遮挡。
- GUI（**未验证**）：桌面中的实际输入、别名对话框、剪贴板内容、导出文件对话框、清空确认、键盘操作。账号页离屏图中的 03 来自本机已有目录（预览读取目录是否存在，不读取内容）。

### 系统设置六分类（2026-10-29，feature/windows-settings）

- 自检（已执行）：`build.ps1 -Preview` 通过，LauncherTests 269 项（新增 23 项设置界面检查）、`--self-test` 179 项（新增 10 项设置模型检查）。系统设置六个分类的离屏图已人工查看，开关、下拉、按钮无遮挡；首轮发现开关文字缩进 14 px，已修正。
- GUI（**未验证**）：桌面窗口中的实际点击与键盘操作；关闭窗口退出／隐藏、托盘图标显隐、退出前未保存提示；开机启动的确认与真实注册表写入（本轮未在真实 HKCU 中写入）及登录后自动打开；恢复上次页面跨重启；最小化窗口不恢复；提示音；导出文件对话框；复制到剪贴板。

### 网络边界（2026-10-29，feature/windows-update-boundary）

- 自检（已执行）：`build.ps1 -Preview` 通过，LauncherTests 246 项、`--self-test` 169 项（删去取较高版本 6 项，新增 URL 白名单、离线拒绝与设置文件检查）、离屏预览四页与两种更新状态。设置页离屏图已人工查看，新开关默认关闭、无遮挡。
- 真实网络只读复核（已执行，构建产物代码经反射调用内部下载函数，不写日志、不落盘）：GitHub `latest-windows.json` 与 `ClaudeLauncherSetup-1.7.3.exe` 的实际跳转链（latest → 标签路径 → 附件主机）均通过白名单，清单验签通过，Setup 2794723 字节、SHA-256 一致；非白名单首次地址在请求前拒绝。
- GUI（**未验证**）：桌面窗口中切换开关并保存、重开后保持、启动时自动检查的实际提示、未开启时手动／托盘检查的提示、有未保存设置时的拦截、下载与安装流程。

### 自检与自动检查（2026-10-28，feature/windows-sync，已执行）

- `scripts/build.ps1 -Preview`：编译通过。`LauncherTests` 241 项断言通过（含四个页面以合成状态布局、删除确认框默认取消、启动探针验证参数和 `CLAUDE_CONFIG_DIR`）。
- `ClaudeLauncher.exe --self-test | Write-Output`：98 项通过，临时目录已清理。
- 离屏渲染四个页面（合成进程状态，不扫描或启动 Claude），人工比对 `docs/assets/界面.png` 与 macOS 四页：布局、插画、文字无裁切。图片在 `artifacts/previews/`，未提交。离屏渲染不代表桌面输入。
- `scripts/setup.ps1`：生成 `ClaudeLauncherSetup-1.6.0.exe`（约 2.9 MB）。静默安装到 `%TEMP%` 随机目录（`/NOICONS`，不运行程序）：文件版本 1.6.0.0；放入一个模拟的 `.previous-*` 更新备份后静默卸载：安装目录被完整移除，`%APPDATA%\Claude-Second`、`Claude-Profile-03`、`%LOCALAPPDATA%\Claude-Data`、`Claude-MultiLauncher` 均保留，卸载注册表项已清除。随后测试覆盖安装：先装到随机目录并放入模拟 `.previous-*` 备份，再静默运行同一 Setup——旧版先被静默卸载（备份被移除），新版装回同一目录，卸载程序仍为 `unins000`；最后卸载，目录与注册表项清除，账号目录保留。启动器运行中时的关闭流程未测（未启动启动器）。
- `scripts/test-install.ps1`：1.2.0 兼容 `--apply-update` 的成功、回退、篡改 3 个场景通过。

#### 1.7.0 更新协议（2026-10-28，已执行）

- `build.ps1`：241 项断言通过；`ClaudeLauncher.exe --self-test | Write-Output`：130 项通过（新增 32 项更新协议检查，见“构建与自检”）。
- `setup.ps1` 生成 `ClaudeLauncherSetup-1.7.0.exe`；临时目录静默安装（版本 1.7.0 / 1.7.0.0）→ 覆盖安装（先卸载旧版、模拟更新备份被清除）→ 卸载（目录与注册表项清除，账号目录、`Claude-Data`、`Claude-MultiLauncher` 保留）；不带 `/RELAUNCH=1` 的静默安装不会启动启动器。
- `test-install.ps1`：3 个场景通过。
- `publish-release.ps1 -DryRun`（一次性测试密钥、4 KB 假 DMG）：构建、签名三份清单并本地验签通过；DMG 哈希不符时中止。测试密钥与暂存目录已删除。
- `updates/server.py`：把 dry-run 产物放进临时目录、回环随机端口启动，六类文件 200 且字节数一致、Content-Type 正确；路径穿越、`server.py`、`latest-linux.json`、错误版本格式均 404。
- 用仓库公钥校验旧服务目录中 1.2.0 的 `latest.json`：通过，确认这把 RSA-3072 密钥就是 1.2.0 客户端信任的发布密钥。

### GUI / 真实实例（本轮未执行）

- 实际窗口中的点击、页面切换、“···”菜单、新建提醒三选项、登录保护进入/结束/重开保留、检查中连点、删除确认与回收站、导出/清空日志、检查更新：**未验证**。
- PID 激活、托盘恢复（用 PID 自身程序重启动、5 秒复查）：**未验证**。
- 真实 Claude 4.29 实例的递归存储检查耗时（Cache 文件较多时）、“Code 已指定”是否在真实 MSIX 启动时出现：**未验证**。
- Code 标签页是否实际使用 `CLAUDE_CONFIG_DIR`、双账号登录隔离、登录回调归属：**未验证**。
- 安装包：开始菜单与桌面快捷方式、交互式向导、启动器运行中时的正常关闭、`/RELAUNCH=1` 重新打开、SmartScreen 实际提示：**未验证**。
- 应用内更新端到端（真实 HTTPS 自建服务与 GitHub 下载、跳转实际路径、下载后运行 Setup、“前往 GitHub 下载”按钮）：**未验证**。自建服务当前 502，GitHub 上尚无 1.7.0 Release。
- 发布脚本正式发布路径（gh 上传草稿、转正式、两来源复核、失败回滚）：**未验证**，本机没有发布私钥，也没有 macOS 草稿 Release。
- 有 Authenticode 签名时的签名者比对：**未验证**（无证书）。
