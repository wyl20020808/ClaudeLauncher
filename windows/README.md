# Windows 版（未开始实现）

按 [跨平台行为规格](../docs/SPEC.md) 原生实现，不与 macOS 共享代码。

## 技术选型（待定，倾向）

C# WinForms，以单文件自包含方式发布，日常运行不依赖 Python、Node.js 或额外安装运行时。

| 功能 | 计划使用的 Windows 接口 |
|------|-------------------------|
| 枚举进程、主程序路径 | `Process` / `QueryFullProcessImageName` |
| 读取命令行 | WMI `Win32_Process.CommandLine` 或 `NtQueryInformationProcess` |
| 按 PID 激活窗口 | `EnumWindows` + `GetWindowThreadProcessId`、`ShowWindow(SW_RESTORE)`、`SetForegroundWindow` |
| 启动实例 | `ProcessStartInfo`（参数 + `CLAUDE_CONFIG_DIR` 环境变量） |
| 单实例 | 命名 Mutex |
| 链接/硬链接检查 | `FileAttributes.ReparsePoint`、`GetFileInformationByHandle` 的 `nNumberOfLinks` |

## 实现前需在 Windows 实机确认

- 官方 Claude 桌面版安装位置与主程序名（Squirrel 安装于 `%LOCALAPPDATA%` 或 MSIX）。
- 默认 userData 位置、是否接受 `--user-data-dir`、是否读取 `CLAUDE_CONFIG_DIR`。
- 主进程与 Helper 的区分方式（预期同为 Chromium `--type=` 参数）。

## 构建与测试

可在 macOS 编写代码；编译、自检和 GUI 验收须在 Windows 上进行，结果分开记录，未执行项标为未验证。
