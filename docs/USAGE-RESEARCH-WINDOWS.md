# 本地界面用量：Windows 调查报告

日期：2026-10-29。分支：`feature/windows-usage-research`。

> **结果（2026-10-29，feature/windows-usage）**：用户授权 D1，D2 不做，D3 未授权。已按 SPEC「本地界面用量」[Windows] 实现。D1 实测（账号 01，Claude 4.29.0 商店版，停在 `claude.ai/epitaxy/<会话>`）：
> - 首次 UIA 连接时只有原生外壳（24 个节点、没有 Code 地址）；约 2.5 秒后网页树可读（532 个节点）。证实 a：连接本身启用网页树。
> - Code 页为 Document，`ValuePattern.Value` 给出地址；层级 Pane×9 → Document → Group×21 → Button。
> - 用量按钮：ControlType Button，支持 ExpandCollapse（Collapsed），名称在 `Name` 中，格式与 macOS 相同：`用量：每周 · 所有模型：##%周二##:## 重置`（五小时窗口尚未开始，数字已打码）。没有用到 HelpText 或子节点。
> - 独立诊断程序（放在临时目录，未进仓库）只读，没有点击、展开或设置任何属性。**问题**：它的打码过滤只排除了 Text 节点，命中关键字的 ListItem 名称（本次对话中我自己回复的几句正文）被原样输出到了诊断结果里。该输出只在本机会话中，没有保存或发送。正式实现不读取 ListItem／Text 等分支的名称，只读可展开 Button 的名称。
> - 正式实现 `--usage-interface-check`：`账号 01：界面已读取；5小时=无；每周=已观察`，约 400～500 ms。
>
> **D3（2026-10-29，feature/windows-usage-popup）**：用户授权。弹窗为 Code 页内的 Window「用量」，含 `https://claude.ai/settings/usage` 链接与 Text／ProgressBar（RangeValue）指标；按钮展开／关闭发出 ExpandCollapseState 属性变化通知。实测端到端：展开 → 通知 → 读到五小时与每周。调查面板时临时程序又输出了几句本会话正文（数字打码，仅本机会话），见 STATUS。
>
> 以下为授权前的原始调查，保留原结论。对应交接 [HANDOFF-windows-1.7.14.md](HANDOFF-windows-1.7.14.md) C4 与 [HANDOFF-windows-sync.md](HANDOFF-windows-sync.md) 第 6 项。

## 本报告的边界

- **只做书面调查，没有写任何用量代码。** 没有用 UI Automation（UIA）、MSAA 或其他辅助技术查询过 Claude 窗口；没有订阅 Claude 进程的任何事件；没有读取 Claude 数据目录中的 Cookies、`Local State`、DPAPI 保护的数据或任何加密数据；没有启动、退出、点击或激活任何 Claude 实例；没有联网。
- 依据：macOS 1.7.9～1.7.14 的实现与 [用量调查](USAGE-RESEARCH.md)、SPEC「本地界面用量（macOS）」、本仓库 Windows 端已有的进程识别代码，以及 Windows／Chromium 的公开行为。凡标“需实测”的内容都还没有在本机验证。
- macOS 的 Safe Storage / lastActiveOrg 授权是用户在 Mac 上的单独授权（AGENTS.md），**不自动延伸到 Windows**。

## 需要你决定的三件事

| 编号 | 决定 | 不授权时的结果 |
|---|---|---|
| D1 | 是否授权一次**只读 UIA 调查**：在你已登录的一个账号中，由启动器以外的独立诊断程序只读 Claude 主窗口的 UIA 树，确认用量按钮／面板的控件类型、名称格式与层级；不点击、不展开、不设置任何属性；你可选择是否由你自己手动展开一次弹窗。输出只含控件结构与是否命中，不含百分比、会话或正文。 | 无法确认 Windows 上能否读到用量控件，功能保持“未接入”。 |
| D2 | 账号归属是否需要**组织指纹**。Mac 用 lastActiveOrg 的 SHA-256 指纹防止同一账号目录内切换组织后串值；Windows 上该 Cookie 由 DPAPI 保护的密钥加密（见下文），读取需要你**单独明确授权**。 | 只用“官方主进程 PID + 进程启动时间 + 固定账号目录”归属：账号之间、进程重启后不会串值；但同一账号目录内切换组织后，旧观察值可能被误当作新组织的值，直到进程重启。可以在界面上注明这一限制。 |
| D3 | 是否沿用 Mac 1.7.11 的“**手动展开官方弹窗时记录一次**”。Windows 等价做法是订阅该 Claude 进程用量按钮的 UIA 展开状态变化事件。 | 只在你主动查看启动器实例页时读取一次（Mac 1.7.10 规则）；周额度只有在那一刻官方控件可读时才能取得。 |

## Mac 踩过的坑在 Windows 上的对应（交接 C4 a～e）

### a. Electron 网页辅助功能树默认关闭

- Mac：只看得到原生窗口外壳，需对已核实的官方 PID 设置 `AXManualAccessibility=true` 才生成网页树（SPEC 允许的唯一用量相关 AX 属性设置）。
- Windows（需实测）：Chromium 在检测到辅助技术客户端时才启用渲染进程的辅助功能树。公开行为是：当客户端对渲染窗口发送 `WM_GETOBJECT` 请求 UIA 根或 MSAA 对象时，Chromium 会切换到辅助功能模式；这本身就是“启用网页树”的动作，**不是纯被动读取**，与 Mac 的 `AXManualAccessibility` 性质相同。
  - 风险：启用后该 Claude 进程在退出前一直保持辅助功能模式，可能增加 CPU／内存占用（Mac 1.7.12 STATUS 已记录同类待决问题）。
  - 不得改用 `--force-renderer-accessibility` 启动参数或修改系统辅助功能设置（会改变官方启动方式／系统设置，违反 AGENTS）。
  - Chromium 新版本存在原生 UIA 提供器与 MSAA/IA2 两条路径，Claude 4.29 使用哪条、首次查询后树需要多久准备好，均**需实测**（Mac 首次构建用了最多 2.5 秒预算）。

### b. 多账号归属

- 只读取已由启动器现有扫描核实的官方主进程（`Core.Scan`：官方安装路径、排除 `--type=` 辅助进程与 Claude Code CLI、按 `--user-data-dir` 映射 01～10）。
- 通过 `UIA ElementFromHandle(Native.WindowFor(pid))` 从该 PID 的顶层窗口进入，读取前后各核对一次 PID、WMI `CreationDate`、账号目录；同账号多个主进程、扫描失败、登录保护中一律不显示。
- 托盘中没有可见窗口的实例：无窗口可读，显示“未观察到”，**不得**为读取而用托盘重启动唤出窗口。
- 缓存键：账号编号 + PID + 进程启动时间 + 目录身份（+ D2 授权后的组织指纹）。任何一项变化即清除，与 Mac 相同。

### c. 两种 Code 页面都要接受

- `https://claude.ai/epitaxy`（Code 首页，未进入对话）与 `https://claude.ai/epitaxy/<会话>`（对话页）都可能有用量按钮。
- Windows 上通过 UIA 文档元素的 `Value`／`Name` 或 `LegacyIAccessible.Value` 取当前地址（**需实测**哪一个可用），只接受上述两种，拒绝嵌套网页与其他域名。会话 ID 只用于判断页面类型，不保存、不输出。

### d. 五小时窗口重置后按钮改显示周额度

- 进行中：按钮名称形如「5 小时限额的 71%3:20 重置」；重置后、发第一条消息前：按钮改为「用量：每周 · 所有模型：11%周三19:00 重置」。解析与展开检测必须两种格式都认。
- 按钮只给周值时，五小时保留上次观察值和原时间，**不推算为 0%**；面板展开时周值必须与按钮一致才采用。
- 英文界面格式 Mac 尚未实测；Windows 同样先只支持已实测的中文格式，未知格式不采用。
- UIA 中按钮文本可能出现在 `Name`、`HelpText` 或子文本节点，拆分方式与 Mac AX 不同，**需实测**；解析器应先用合成字符串离线测试。

### e. 显示与性能

- 卡片只显示观察时间（HH:mm:ss，跨日为 MM-dd HH:mm），两项各自独立时间；读取中保留旧值，不闪成“—”。
- 每次跨进程 UIA 调用必须有短超时：UIA 客户端调用默认可能阻塞数秒，Claude 卡顿时会拖住启动器。实现应在后台 STA 线程使用 `IUIAutomation`（`CUIAutomation8`）并设置 `ConnectionTimeout`／`TransactionTimeout`（约 100 ms），整次读取总预算约 1.5 秒（首次启用网页树最多 2.5 秒），遍历深度与节点数设上限，超时不采用新结果。
- 只用 `CacheRequest` 批量取 `Name`、`ControlType`、`ExpandCollapseState` 等少量属性；不读取文本框内容、聊天正文或输入框。
- 不调用 `Invoke`／`Expand`／`SetFocus` 等任何会改变官方界面的模式方法。

## 组织指纹（仅在 D2 授权后才可调查）

- Windows 版 Chromium 的 Cookie 值使用 AES-256-GCM 加密，密钥保存在账号目录 `Local State` 的 `os_crypt.encrypted_key`，由当前用户的 DPAPI 保护；新版 Chromium 还可能使用 App-Bound Encryption（需高权限服务解密），Electron 应用是否启用**需实测**。
- 若授权，读取范围应与 Mac 完全对齐：只读 `Cookies` 中 `name = lastActiveOrg` 且域名为 `claude.ai`／`.claude.ai` 的一行；只解密该行；只保留 SHA-256 指纹，不保存原值、不输出、不读取任何登录 Cookie、不复制数据库、不建锁或日志；数据库被占用或处于 WAL 状态时拒绝而不是复制。
- 授权前**不读取** `Cookies`、`Local State`、DPAPI 数据或任何加密内容；本报告也没有读取过。

## 不做的事

- 不请求 Claude／Anthropic，不调用官方 IPC、`get_usage`、`readPlanUsage`、`PlanUsage.nudgeRefresh`，不复用登录凭据，不启用调试端口，不注入进程。
- 不读取 `plan-usage-history.json` 作为回退（Mac 1.7.9 起已停用历史路径）。
- 不代为展开官方弹窗；不后台定时读取；不在启动器以外的应用中监听键鼠输入。

## 若授权后的实施顺序（建议）

1. D1 只读 UIA 调查，用独立诊断入口（如 `--usage-interface-check`，只输出账号编号、是否命中、耗时），写回本报告。
2. 用调查得到的控件结构写合成解析器与自检（两种按钮格式、面板一致性、两种页面地址、多账号隔离、超时）。
3. 接入实例卡片（按 SPEC 的主动查看时读取一次），关闭“显示本地界面用量”设置时停止读取并清空缓存；观察记录保存到启动器自己的文件。
4. 视 D2、D3 决定是否加入组织指纹与展开事件。

每一步的自检与真机实测分开报告；未执行的标“未验证”。
