# 开发与验证

## 实现入口

`src/main.m` 包含目录映射、进程参数解析、主进程扫描、存储路径校验、AppKit 窗口、启动与 PID 激活、自检。当前项目规模小，暂不拆分为多个框架或引入依赖。

- `ProfilePath`：固定编号到目录的映射。
- `CodeConfigPath`：02～10 的 Claude Code 配置目录，启动时作为 `CLAUDE_CONFIG_DIR` 传入；01 返回 nil，沿用 `~/.claude`。
- `Scan`、`AccountForArguments`、`IsMainExecutable`：区分官方主进程、辅助进程及 CLI。
- `ValidateProfile`：路径和核心存储元数据检查。
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

## GUI 验收

在用户授权范围内，仅使用少量实例：验证打开运行中账号不新增进程；未运行账号正确启动；同账号连点不重复启动；隐藏和最小化后恢复正确 PID；重开启动器只显示一个管理窗口；退出启动器后 Claude 继续运行。首次创建测试须使用确认未使用的账号目录，不登出、删除或覆盖真实账号数据。

登录测试需用户自行登录；没有真实双账号验证时，不得宣称登录不会串号。历史报告中的通过项不自动适用于新构建。

## 打包与签名

`scripts/package.sh` 仅校验并压缩已构建应用，不重新签名或启动应用。修改应用后先重新构建，验证后再打包。

当前采用 ad-hoc 签名，designated requirement 依赖代码哈希。代码变化可能使 macOS 辅助功能授权失效。不要通过修改 TCC 数据库、放宽签名要求或绕过权限解决。稳定签名方案需要另行设计与用户正常授权；不可把重启 Mac 当作已证实的解决办法。
