# Windows 侧栏工具直接调用调查

调查始于 2026-11-04，2026-11-05 补充真实界面验证。目标是在同步完成后直接建立原生分组，省去用户发送模型消息的步骤。检查对象为本机商店版 Claude 4.16336.0.0。

## 已执行的验证

只读检查安装包，并运行 `scripts/check-sidebar-direct-research.cjs`。脚本对五个代码分块做 SHA-256 校验，通过 Node 内置解析器选取函数，在内存中的 JS context 里运行。程序包代码不落盘，账号数据、真实会话、Claude 进程和网络均不参与测试。

11 项断言通过。官方工具处理器能通过注入的调度函数建立合成分组，校验空名称和超长名称。官方界面处理器会检查登录分区，并调用分组变更统计函数。官方调度函数在模拟的无窗口环境下返回失败。官方服务端序列化函数会把合成分组的名称和 ID 纳入同步数据。

这些结果只验证选定的官方函数。分组存储、窗口、调度和选择器依赖使用合成实现，因此不能视为外部调用入口、真实账号建组、重启保存或零网络请求验收通过。

## 调用链和限制

1. `index.chunk-CdwXqeBD.js` 的 `J_` 注册 `ccd_sidebar`，`q_` 处理 `create_group` 等工具调用。`Be` 获取主窗口的 `LocalSessions` dispatcher，再发出 `dispatchSidebarCommand`。
2. `index.chunk-YLCRWeby.js` 的 `InternalMcpServerManager.createProxyServers` 用 `createSdkMcpServer` 把工具挂到会话 SDK。所检查的注册路径没有建立外部可直接连接的独立监听服务。尚未找到启动器可用的稳定外部入口。
3. `mainView.js` 的 `onSidebarCommand` 监听 Electron 内部消息。`shared-22-BACJUrcb.js` 的 `CR` 取得当前窗口的分组 store，调用 `addCustomGroup`，随后调用统计函数。
4. `shared-140-DfG6VwpK.js` 的 `rp` 生成 `claudeai.epitaxy.sidebar.custom_group_changed` 事件。隔离测试捕获了该事件。真实统计传输受客户端运行状态影响，本轮没有实测发送。
5. `shared-21-CuohtEoj.js` 把 `yy` 注册为 `serverPartialize`。它会输出 `customGroups`，以及经过筛选的归属、顺序和区块数据。即使所有会话归属均为空，分组名称和 ID 仍进入输出。
6. `shared-5-u8kha2eH.js` 的设置同步代码包含对 `/api/claude_code/organizations/${org}/user_settings` 的 GET 和 PUT。运行时存在身份、同步状态等条件，因此不能把代码路径存在解释为每次调用都立即发请求，也不能承诺建组全程零请求。

## 方案判断

- 直接复用会话内部工具可以绕过模型推理，但外部进程接入仍需官方桥接入口。此次检查没有找到适合启动器使用的入口。
- 补查官方 `claude://code/…` 分派器，可见新建、继续、待处理和搜索等入口；所检查的分派代码没有提供创建分组或移动会话的命令，现有预填链接不能直接升级为建组调用。
- 向窗口注入代码或开启调试入口会增加对官方客户端运行方式的依赖，超出当前最小接入方案。此次没有实施。
- 重试写配置或界面本地存储仍面临官方协调同步。此前两次覆盖失败记录继续有效，完整 `dframe-store` 的外部改写也没有获得真实验收。
- 官方界面自动建组有望省去模型消息，仍会经过官方保存和统计路径。2026-11-05 用户进一步明确讨论边界，启动器只在本机模拟用户操作，网络请求由官方客户端按自己的登录状态和原生逻辑构造、发送。启动器本机自动化与完整流程零网络请求是两个不同条件，后续按前者验证。

当前产品继续使用已有的预填流程。本轮没有修改启动器功能。官方界面建组、移动会话和重启保存已在本机账号 02 验证；稳定外部工具入口、启动器自动化实现和客户机行为尚未验证。

## 2026-11-05 界面验证进展

读取全局策略得到 `mode=idle`。通过启动器的账号 02 入口和正常启动确认，已打开一个官方 Claude 窗口。窗口观察时存在遮挡，需要置前才能可靠进行后续操作。

自动审批曾两次拒绝 `sky.activate_window`，理由为 idle 禁止窗口和焦点交互。这与本次提供的规则中 work 模式延后桌面交互的定义不一致。当时暂停输入，随后用户明确回复“允许切到前台并继续验证”，再次执行成功。

在账号 02 的官方 Code 侧栏完成以下真实窗口操作：

1. 通过官方“新建部分”菜单创建 `启动器验证-20261105`，侧栏显示为空分组。
2. 将“测试2”中的一条同步副本通过“移至分区”菜单移动到验证分组，界面确认归属变化。
3. 通过官方“菜单 → 文件 → 退出”正常退出。启动器显示无运行账号后，再从启动器打开账号 02。
4. 重启后的官方窗口仍显示验证分组，且测试会话仍在组内，确认本机正常重启后保留。
5. 通过同一官方菜单把测试会话恢复到原来的“测试2”，确认验证分组为空。用户明确同意删除后，通过官方“删除分区”确认框清理了 `启动器验证-20261105`；确认框明确提示“其中没有任何内容”。删除后的真实窗口确认验证分组消失，“测试2”及其中的测试会话仍在。

全过程没有发送模型消息，没有由测试脚本构造 Claude HTTP 请求，也没有读取登录凭据或改写官方配置。操作经本机 UI 自动化触发，保存、同步和统计由官方客户端自身逻辑处理。本轮未抓取网络流量，因此不报告实际请求次数或零请求结论。

这次结果证明官方界面路线在该版本、本机单会话场景可用，不代表启动器已接入一键建组。批量会话、同名标题、界面版本变化和中断恢复尚未验证；不能把当前坐标操作直接视为可交付的启动器实现。

## 2026-11-05 接入前的会话定位检查

现有预填指令只按完整会话 ID 匹配。官方侧栏辅助功能树主要暴露标题，直接按标题点菜单会降低同名会话的定位可靠性，因此尚未据此替换产品流程。

补查 `index.chunk-Bqw5pOHo.js`：官方分派器接受 `claude://code/continue?session=<sessionId>`。`xjr` 调用 `Ejr` 等待会话加载，并在未归档记录中按 `sessionId` 严格相等查找；仅参数为 `last` 时才选最近会话。明确 ID 不存在时返回空，外层导航到 Code 首页。该路径没有构造模型消息，但仍由官方客户端处理导航与统计。

开发探针新增四项隔离断言：相同标题下按 ID 定位、缺失 ID 不回退到其他会话、归档会话不选中、每次等待列表加载。合计 15 项通过。此结果只证明该函数在合成依赖下的行为，真实链接转交、打开会话后的分组菜单绑定与批量自动化尚未验证。

用户随后明确选择“必须后台完成，不操作窗口”。因此排除将界面自动化接入启动器的方案；前面的真实窗口实验只保留为历史验证，不视为满足最终要求。当前没有修改 Windows 产品代码。

## 2026-11-05 后台入口复查

本轮只读检查安装包并运行隔离探针，没有操作官方窗口、启动 CLI、读取账号凭据或向账号服务发送请求。

- `InternalMcpServerManager.createProxyServers` 把 `ccd_sidebar` 注册为 SDK MCP 对象，处理器绑定在当前桌面会话上下文中。所检查的注册路径没有建立可供外部进程连接的 HTTP 服务或命名管道。
- 补查官方 SDK 分块 `index.chunk-Cyq499W4.js`：`createSdkMcpServer` 对应 `Hn`，返回 `{type:"sdk", name, instance}`；`connectSdkMcpServer` 将该实例连接到 `Qe` 内存传输对象。`Qe.send` 调用进程内回调，随后 `sendMcpServerMessageToCli` 通过已建立的会话控制流传递 `mcp_message`。这不是独立的外部 MCP 服务地址。
- 官方分派函数 `Be` 只检查主视图的 `webContents` 和内部 dispatcher，本身不要求窗口置前。新增三项隔离断言：不提供显示／置前方法仍能分派，renderer 已销毁时拒绝，内部 dispatcher 缺失时拒绝。总计 18 项通过。依赖仍为合成对象，不能视为启动器已连接真实官方进程。
- 官方公开文档将 Desktop 的 scripting/automation 标为不提供；另有 `claude mcp serve`，用于向 MCP 客户端开放 Claude Code 工具，但该说明没有提供接入现有 Desktop `ccd_sidebar` 的方法。此处只查文档，没有启动该命令。参见 [Desktop 功能比较](https://code.claude.com/docs/en/desktop#feature-comparison) 与 [Claude Code 作为 MCP server](https://code.claude.com/docs/en/mcp#use-claude-code-as-an-mcp-server)。

结论：后台执行的内部函数确实存在，当前缺口是从启动器进入官方进程的可靠、可用入口。已检查的 SDK、内部 IPC 和链接入口均未打通这一步；不能把函数级隔离测试当成可交付的后台功能。未实施进程注入、客户端改写或调试入口改动。原有预填流程暂时保留，新的后台自动分组尚未实现。

## 复现

在已安装相同版本的 Windows 开发机执行 `rtk proxy node scripts/check-sidebar-direct-research.cjs`。只需本机 Node，无安装或下载步骤。程序包升级导致哈希变化时，脚本直接拒绝执行，需先重新审查。

| 程序包内文件 | SHA-256 |
| --- | --- |
| `app.asar/.vite/build/index.chunk-CdwXqeBD.js` | `2ade0ec37b2441110079a9e288983937b10c7925da50ba03f3032ec822f50af9` |
| `app.asar/.vite/build/index.chunk-YLCRWeby.js` | `d61c7ab6d0dbdd154e06ec253826f95135b158edff86db0286e8ec6d29df3722` |
| `app.asar/.vite/build/index.chunk-Bqw5pOHo.js` | `256c649a0a7e02a1e413ff957aec93aec0f89bd5938dad69f8318652a4adb4fd` |
| `app.asar/.vite/build/index.chunk-Cyq499W4.js` | `80a47f511eaa0517955b83f891779719d40609e20ccf00a645804f22d39e8c6e` |
| `ion-dist.asar/assets/v1/shared-22-BACJUrcb.js` | `6629e87cd46c19aaace5887518795ac2e1417a958d4849ebe4f63c52176fdd3e` |
| `ion-dist.asar/assets/v1/shared-140-DfG6VwpK.js` | `c23715333142677854aedb06a57a4993941dbe8da6b7dba8f687d7d8428433d3` |
| `ion-dist.asar/assets/v1/shared-21-CuohtEoj.js` | `8513ed8b2bdb0e469685841a90fb35d63529a0c18733e6fd20f3d7a96f3a765d` |
| `ion-dist.asar/assets/v1/shared-5-u8kha2eH.js` | `7e36ddc101d5b0a8b4427f9b882472b1f01636a60aa22fbb6062f45292f4e7e7` |
