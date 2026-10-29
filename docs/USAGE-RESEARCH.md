# 五小时／周用量：本地被动读取调查

日期：2026-10-28。分支：`codex/usage-limits-research`，基于本地 dev `432d2d5`。本机官方 Claude 版本：4.29.0。结论仅适用于本次检查的版本，不表示 Anthropic 承诺支持本地历史格式。

检查对象 `app.asar` 的 SHA-256：`fea39d0330a1f6dcb143d9a3fe7b45ed21343c5fc2895c1dff2cb942a28eeb7a`，用于识别研究版本。

## 当前组织匹配修复（2026-10-28）

- 用户明确要求匹配当前对应组织，不接受“全历史中取最新一条”作为替代。该试验改动已撤回，未提交、推送或发布；试验构建移到 `build/rejected-unmatched-usage/`，不得用于交付。下述修复使用真实本地组织选择标识，不沿用该试验。
- 官方 4.29.0 的 `getLastActiveOrg`（包内 `Jj`）先检查内存与部署覆盖值，否则读取当前 Electron 会话中、官方域名下名为 `lastActiveOrg` 的 Cookie；用量请求的组织参数来自此值。它是组织选择标识，不是用于登录的 `sessionKey` 或 OAuth Token。
- 本轮对两个本地目录的 Cookies 数据库仅作只读、不可变快照的定向存储诊断，查询条件固定为 `name = lastActiveOrg` 且域名为 `claude.ai`／`.claude.ai`，只读取该行的值长度及持久化标记，不读取任何登录 Cookie 值。两目录均有一行，明文长度为 0、密文长度为 83。没有读取或输出密文、密钥或组织 UUID。不可变快照忽略 WAL，只用于判断存储方式，不能作为实时组织选择依据。
- `fcache` 虽含组织 UUID 与时间，但官方也会用当前 `lastActiveOrg` 核对它并丢弃不匹配缓存；不能反过来拿该缓存证明当前组织。`lastKnownAccountUuid` 是账号标识而非当前组织，旧会话目录也不能证明当前组织。上述候选均不能替代明确匹配。
- 用户随后明确授权“允许仅本地解密组织标识”。实现只访问 `Claude Safe Storage` / `Claude Key`，仅解密上述选择行，用毕清除可控密钥缓冲区；不读取登录 Token，不调用官方 IPC，不启用调试端口，不发送 Claude 请求。
- 本机两个数据库均为 schema 24、普通回滚日志格式，journal 为 0 字节，无 WAL。实现以已验证文件描述符通过 SQLite readonly + immutable 打开，拒绝 WAL 模式和活动日志，并在前后检查数据库 inode、大小、修改时间及路径；不复制数据库、不写账号目录、不创建锁或日志。查询仅选择 lastActiveOrg 所需字段，不返回其他 Cookie 行，限制查询权限、大小和步骤数。
- 解密沿用公开的 [Chromium macOS v10 格式](https://raw.githubusercontent.com/chromium/chromium/138.0.7204.49/components/os_crypt/sync/os_crypt_mac.mm)：PBKDF2-HMAC-SHA1、1003 次、AES-128-CBC；验证 [Cookie schema 24 的域名 SHA-256 前缀](https://raw.githubusercontent.com/chromium/chromium/main/net/extras/sqlite/sqlite_persistent_cookie_store.cc) 和 UUID 格式。只支持已验证格式，未知格式不猜测。
- 对历史先按该组织筛选，再取其最新样本。其他组织即使更新也不会被采用；当前组织无记录、多个有效选择行不一致或读取前后切换，均不显示数值。15 分钟新鲜度门槛保留。已落盘选择标识不是官方内存实时状态，也不是登录验证；官方尚未落盘的切换无法通过本地文件即时获知。
- 验证：arm64 / x86_64 各 9 组自检通过，包含独立 OpenSSL 合成密文向量、域名绑定、错误密钥、其他组织更新、无匹配、选择行过期、并发修改、WAL／日志与路径安全检查。测试只建立合成目录。
- 真实目录诊断在 `sandbox-exec` 禁止所有网络、禁止写入整个 Application Support 的约束下通过。01、02 均精确匹配到组织历史，当时采样分别约 36.8、48.2 分钟前，故返回“记录已过期”。02 读取约 21.9 ms；01 的约 69.7 秒含系统钥匙串人工授权等待，不能当作稳态读取耗时。首次另一次读取在授权等待期间遇到数据库变化，正确拒绝结果。
- 1.7.7 离屏合成渲染已查看，数值、来源时间、无匹配状态均无重叠。未执行 1.7.7 的真实 GUI 激活／安装／应用内更新；用户要求发布远端后自行通过自动更新安装。固定签名证书未变；新增的系统钥匙串访问提示与辅助功能权限无关，不自动更改访问控制。
- 下文“结论”至“实现与验证入口”为初版调查记录，保留当时的安全边界与验证结果；修复后的实现及发布状态以本节和 STATUS 为准。

## 结论

采用用户指定的被动方案：启动器切到前台时，只读取官方客户端已经保存的用量历史。功能不发 HTTP 请求、不提取凭据、不驱动官方客户端刷新、不注入代码；因此不会新增限额查询流量或模型调用。对账号总体封禁概率没有可量化证据，不能承诺零风险。

此方案的主要限制是新鲜度和身份归属，而非速度。历史文件可能在切换组织或退出登录后保留；本机一个目录含三个组织、最后记录已旧约 3.5 小时，不能直接把末条数据当作当前登录账号额度。实现对多组织／未知组织隐藏数值，单组织也明确标为“本地记录”，不声称当前身份已核实。15 分钟是本实验版的保守显示阈值，不是官方风控阈值。

## 官方自身怎么查

只读 `/Applications/Claude.app/Contents/Resources/app.asar` 内已有 JavaScript，未修改、复制或重新签名官方应用，未读取用户凭据。检查到两条不同路径，不能混为同一接口：

1. 桌面主进程的套餐用量路径：`/api/organizations/<组织>/usage`，由 Electron 网络会话发送。结果包含用量与重置时间；主进程 `plan-usage` 数据提供器只保留内存值，没有一个同名完整磁盘缓存。官方代码有 15 分钟／近期交互后 5 分钟的后台调度、空闲和菜单使用条件；具体是否启用由官方配置决定，不能据常量推断本机每次请求发生时间。
2. `readPlanUsage` 路径：`/api/oauth/usage`，通过官方程序内部获取 Claude Code bearer，带 OAuth beta 头。成功结果缓存 60 秒，失败结果缓存 20 秒，10 秒超时。代码所在文件为 `.vite/build/index.chunk-BL-_OoWD.js`。这证明内部存在查询实现，不证明它是允许第三方调用的公共接口，也未证明点击本次弹窗一定经过此路径。

主进程会把部分结果写入账号目录根下的 `plan-usage-history.json`。本机 v2 格式为 `version`、`samples`；单条含毫秒采样时间 `t`、组织 `org` 和用量映射 `u`，`fh` 对应五小时、`sd` 对应七天。按同一组织至少间隔 270000 毫秒（4.5 分钟）追加，保留约 30 天。历史不保存重置时间。相关代码位于 `.vite/build/index.chunk-C7NkbHFq.js`。

内部 `PlanUsage.nudgeRefresh` 是官方渲染进程／主进程间的 IPC，不是本轮发现的外部公共接口。为调用它而注入客户端、启用调试端口或提取会话，不属于本方案。用户在官方界面主动查看用量可以让官方自己处理，但不保证新的值立即写入历史。

官方还提供 Claude Code 状态栏 `rate_limits` 数据，适合终端会话的被动显示；官方说明状态栏本地运行不消耗 API tokens。但数据在首个 API 响应后才出现，不能为此额外发模型请求，也没有证据证明桌面界面会运行终端状态栏命令。因此本轮没有改用户的 Code 配置。[官方状态栏说明](https://code.claude.com/docs/en/statusline)

## 费用与风险

- 被动读取：每次切前台只做本地文件读取，新增官方限额查询数为 0，新增模型调用数为 0；没有这项功能产生的 token 计费。原有应用更新检查与官方客户端自己的网络行为不计入“新增限额查询”。
- 每分钟直接查询：全天单账号 1440 次；仅活跃 8 小时为 480 次；十个账号分别为 14400／4800 次。这里只是频率乘法，不是允许的服务端请求上限。
- 每次切前台直接查询：次数取决于切换频率，需要缓存／防重入；降低次数不能证明认证复用或自动化访问被允许。
- 官方认证说明限制第三方收集、保存、转发 Claude.ai 凭据或会话 Token。复刻相同 URL 与请求头不会使第三方程序自动获得官方客户端的授权地位。本方案不走这种认证复用路径。[官方认证与合规说明](https://code.claude.com/docs/en/legal-and-compliance)
- 官方推荐通过 Settings > Usage 查看会话与周用量。[官方用量说明](https://support.claude.com/en/articles/9797557-usage-limit-best-practices)
- 本地只读功能没有新增服务端请求这一风险来源，但官方没有给出“该第三方启动器读取本地历史绝不封号”的保证。本轮不做规避检测、不伪装官方程序、不测试封禁阈值。

## 实测与限制

- 官方 GUI：点击 Code 用量弹窗，至自动化观察到五小时／周数据约 919 毫秒，包含点击和辅助功能读取开销，可能命中缓存；不是纯网络请求延迟。
- 原生安全读取诊断：账号 01 为“身份待核对”，1.931 毫秒；账号 02 为“本地记录”，0.310 毫秒；未创建的 03～10 为 0.041～0.072 毫秒。各目录仅读取一次，无凭据、组织标识和百分比输出。
- 新版真实 GUI：启动后显示账号 01“身份待核对”、账号 02 的本地数值与时间；从账号管理返回实例页，至观察到用量区域约 501 毫秒，包含自动化开销，不能当作本地文件读取耗时或服务器新鲜度。
- 离屏截图：使用合成 62%／70% 与身份待核对两种状态，查看确认文字、进度条和来源时间未重叠。这些合成数字不是实测账号值。
- 没有读取 API 密钥／Cookie／Token，没有发送测试聊天，没有更换、退出或重启 Claude，没有验证账号真实登录身份、所有订阅类型或其他 Claude 版本的历史兼容性。

## 实现与验证入口

`src/UsageHistory.m` 是 Foundation／POSIX 只读实现，无 Python／Node 运行依赖。文件和各级目录禁止链接及不安全权限，拒绝硬链接、非常规文件和大文件；未知格式、未来时间、过期、多组织记录不显示百分比。读取结果不持久化，原文件不写回。

```sh
SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh build/usage-check
arch -x86_64 'build/usage-check/Claude 启动器.app/Contents/MacOS/ClaudeLauncher' --self-test
'build/usage-check/Claude 启动器.app/Contents/MacOS/ClaudeLauncher' --usage-history-check
'build/usage-check/Claude 启动器.app/Contents/MacOS/ClaudeLauncher' --snapshot build/usage-check/usage.png 0 --usage-demo
```

不应为验收而反复查询官方接口。测试用随机合成目录，测试结束删除合成目录；真实账号目录只读。发布／安装状态另见 STATUS，本研究不自动合并主分支或发布更新。
