# 开发与验证

## 实现入口

`src/main.m` 包含目录映射、进程参数解析、主进程扫描、存储路径校验、AppKit 窗口、启动与 PID 激活、自检。当前项目规模小，暂不拆分为多个框架或引入依赖。

- `ProfilePath`：固定编号到目录的映射。
- `CodeConfigPath`：02～10 的 Claude Code 配置目录，启动时作为 `CLAUDE_CONFIG_DIR` 传入；01 返回 nil，沿用 `~/.claude`。
- `Scan`、`AccountForArguments`、`IsMainExecutable`：区分官方主进程、辅助进程及 CLI。
- `ValidateProfileAtPath` / `ValidateProfileAsync`：完整递归元数据检查及后台调度；回调返回主线程。`ValidateProfileRoot` 用于操作前快速复核固定根路径。
- `LoginBlocker` / `openingBlocker:expectedPID:`：登录准入、检查完成后的进程与保护状态复核；`checking` 集合按账号防止检查期间重复操作。
- `configuredApps`：仅保留本次启动器返回的、启动时间与主程序/账号参数已核对的新进程对象。它证明已指定配置，不证明 Code 实际写入，不能持久化 PID 后冒充已验证状态。
- `Launcher`：界面、启动状态与单实例管理。
- `activatePID:account:`：校验 PID 后激活、尝试恢复窗口。
- `SelfTest`：合成数据测试，不读取真实凭据。

## 构建

运行 `scripts/build.sh`，默认输出到项目 build/。可传入相对项目根目录或绝对输出路径。不要指定日常正在运行或已授权应用所在目录。

构建执行 clang、Info.plist 校验、临时本地签名、签名校验和内置自检。官方图标在构建时从本机 Claude.app 复制。版本号与 build 号位于脚本生成的 Info.plist 内。

单独执行自检：

```sh
./build/'Claude 启动器.app'/Contents/MacOS/ClaudeLauncher --self-test
```

自检在当前工作目录创建随机合成目录并清理，仅覆盖参数解析、路径及存储校验等逻辑。不能代替真实窗口测试。

隔离加固自检增加登录准入、检查中删除拦截、保护状态变化、原 PID 退出/替换、保守 Code 状态、并行后台检查的主线程回调，以及深层 Code 存储链接拒绝。并发测试只检查随机合成目录，失败不会污染另一个合成账号的结果；不创建真实账号，不读写登录数据。

先在独立目录验证：`SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh build/isolation-hardening-check`。需要更新日常 build/ 时，先提醒用户退出并重开启动器才使用新版，再执行同一签名命令且不传输出目录。本轮按用户限定不修改构建脚本，版本号仍为 1.6 / build 7，以分支和提交区分。

## GUI 验收

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

`ClaudeLauncher --snapshot <输出.png> [页码]` 在后台渲染窗口到 PNG，不占单实例锁、不启动 Claude。页码：0 实例管理、1 账号管理、2 日志记录、3 系统设置。从终端运行时辅助功能状态按终端判断，可能显示未授权。插画位于 `resources/ui/`，构建时复制进应用。

## DMG

`scripts/dmg.sh [应用路径] [输出路径]` 把已构建应用和“应用程序”快捷方式打进只读压缩 DMG（UDZO），设置 `SIGN_IDENTITY` 时同时给 DMG 签名，最后 `hdiutil verify`。不重新构建、不修改应用。构建为通用二进制（arm64 + x86_64），Intel 自检可用 `arch -x86_64 … --self-test` 在 Rosetta 下运行。未做 Apple 公证；macOS 27 上 `hdiutil create/attach` 会提示已弃用但仍可用。
