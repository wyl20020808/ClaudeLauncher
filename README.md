# Claude 启动器

[![macOS release](https://img.shields.io/github/v/release/wyl20020808/ClaudeLauncher?filter=macos-v*&sort=semver&label=macOS&color=a8765a)](https://github.com/wyl20020808/ClaudeLauncher/releases/latest)
![platform](https://img.shields.io/badge/macOS-12%2B-lightgrey)
![platform](https://img.shields.io/badge/Windows-10%202004%2B-lightgrey)
![language](https://img.shields.io/badge/Objective--C-AppKit-blue)

在一台电脑上同时管理多个 Claude 桌面账号的原生启动器。每个账号使用独立的数据目录启动同一份官方 Claude.app，不复制、修改或重签官方应用。

> 非官方工具，与 Anthropic 无关联。所有管理功能只在本机执行，不向 Claude／Anthropic 发送任何请求。

![Claude 启动器 macOS 实例管理页](docs/assets/screenshot-macos.png)

<sub>截图为离屏渲染，账号与用量均为合成数据。</sub>

## 目录

- [功能](#功能)
- [安装](#安装)
- [使用](#使用)
- [隐私与本地边界](#隐私与本地边界)
- [账号隔离说明](#账号隔离说明)
- [开发](#开发)
- [项目结构](#项目结构)
- [文档](#文档)

## 功能

| 模块 | 说明 |
|------|------|
| 实例管理 | 最多 10 个账号卡片（01～10），显示运行状态；按官方主进程 PID 精确激活窗口，未运行时启动，无目录时创建。 |
| 本地用量 | 读取官方 Claude Code 界面上已有的五小时／每周限额控件，按账号分别记录观察时间。 |
| 账号管理 | 搜索、状态筛选、本地别名、多选复制目录路径；登录保护；删除仅移到废纸篓。 |
| 项目管理 | 统一的项目目录清单，可识别并导入各账号本机记录过的目录，选择账号后打开。 |
| 日志记录 | 启动器自身日志持久保存，按时间／实例／级别筛选、搜索、分页、导出。 |
| 系统设置 | 关闭行为、菜单栏、启动页、刷新频率、插画、通知、日志上限、登录启动（开关）、界面语言（简体中文／English，macOS）、手动更新。 |
| 软件更新 | 默认关闭；开启后仅从本项目 GitHub Releases 检查，下载后验签再安装。 |

## 安装

### macOS

1. 从 [Releases](https://github.com/wyl20020808/ClaudeLauncher/releases/latest) 下载 `ClaudeLauncher-<版本>.dmg`。
2. 打开 DMG，把「Claude 启动器」拖到「应用程序」。
3. 首次打开若提示无法验证开发者：在 Finder 中右键应用选「打开」，或到「系统设置 → 隐私与安全性」点「仍要打开」。

要求：macOS 12 及以上，Apple 芯片或 Intel（通用二进制），本机已安装官方 Claude 桌面版。恢复最小化窗口和读取本地用量需要你在「隐私与安全性 → 辅助功能」中自行允许本应用，程序不会自动修改权限。

> 应用使用固定的自签名证书签名，未经 Apple 公证。证书不变时，更新后辅助功能授权会保留。

### Windows

Windows 版为独立的原生实现（C# / WPF），安装包 `ClaudeLauncherSetup-<版本>.exe` 同样发布在 Releases 中，需要 Windows 10 2004（19041）及以上。构建与验证见 [windows/README.md](windows/README.md)。当前发布版为 1.8.4，发布与待验证事项见 [项目状态](docs/STATUS.md)。

## 使用

- **启动账号**：在实例管理页点卡片上的「启动」，运行中的账号显示「打开」并切换到对应窗口；「+ 新建实例」创建第一个未使用的编号。
- **登录新账号**：在账号「···」菜单进入「登录 / 重新登录保护」，其他 Claude 正常退出后登录，核对身份后结束保护。
- **查看用量**：在实例管理页聚焦或点击时读取一次；你在 Claude 中手动展开 Code 用量弹窗时，也会为该账号记录一次。卡片显示的时间是本机观察时间，不是服务器实时额度。
- **项目**：「项目管理 → 添加目录」或「识别并导入」，点「打开」选择账号后，在 Claude 中选择复制好的文件夹。

完整说明见 [使用说明](docs/使用说明.txt)。

## 隐私与本地边界

- 设置、账号、项目、日志和用量功能只读写本机数据，不向 Claude、Anthropic 或其遥测／登录服务发送请求，也不通过 CLI、浏览器或代理间接触发查询。
- 不读取、复制或打印登录凭据。用量归属只对 Cookies 中的非登录字段 `lastActiveOrg` 计算本地指纹，不解密，不访问钥匙串。
- 用量只读取官方界面已显示的控件：不轮询、不代点、不打开弹窗。必要时会开启对应官方进程的本机辅助功能树（`AXManualAccessibility`），使第二个及以后的进程可读。
- 唯一的联网功能是软件更新：默认关闭，固定只访问本项目 GitHub Releases，清单经 RSA-3072 签名校验。
- 启动器数据保存在 `~/Library/Application Support/Claude Launcher`（项目清单、日志、运行锁），偏好与别名保存在本机用户偏好中。

完整规则见 [SPEC](docs/SPEC.md) 与 [AGENTS.md](AGENTS.md)。

## 账号隔离说明

| 账号 | 数据目录（`~/Library/Application Support/` 下） | Claude Code 配置 |
|------|------|------|
| 01 | Claude（官方默认目录） | 默认 `~/.claude` |
| 02 | Claude-Second | `<账号目录>/ClaudeCodeConfig` |
| 03～10 | Claude-Profile-03 ～ Claude-Profile-10 | `<账号目录>/ClaudeCodeConfig` |

- 已有目录直接复用，固定编号不迁移、不合并。删除仅通过「···」菜单二次确认后移到废纸篓；01、运行中或启动中的账号不可删除。
- 独立目录**不是安全沙箱**：系统权限、钥匙串加密设施、浏览器登录回调、`~/Library/Logs/Claude`、网络与本地服务仍可能共享。
- 项目文件由同一 macOS 用户下的所有账号共享，包括 `CLAUDE.md`、`.claude/settings*.json`、`.mcp.json`。同一项目请轮流使用，启动器无法检测同时编辑。

## 开发

需要 macOS、Xcode Command Line Tools，以及 `/Applications/Claude.app` 或 `~/Applications/Claude.app`。日常运行与构建不依赖 Python 或 Node.js。

```bash
SIGN_IDENTITY="ClaudeLauncher Local Signing" /bin/zsh scripts/build.sh
```

构建输出到 `build/Claude 启动器.app`，并自动运行内置自检。也可以单独运行：

```bash
"build/Claude 启动器.app/Contents/MacOS/ClaudeLauncher" --self-test
```

| 命令 | 用途 |
|------|------|
| `scripts/build.sh [输出目录]` | 构建通用二进制并运行 `--self-test` |
| `ClaudeLauncher --ui-self-test <目录>` | 原生控件自检，并离屏渲染各页面截图（独立偏好、合成数据） |
| `scripts/dmg.sh` | 生成拖拽安装的 `build/ClaudeLauncher-<版本>.dmg` |
| `scripts/publish-macos.sh [--dry-run]` | 构建、签名清单并发布到 GitHub Releases |

重新构建不会更新 `/Applications` 中的已安装版本。日常开发在 `dev` 分支，验证后合并到 `main`；发布需手动运行发布脚本。签名、发布与验证流程见 [开发流程](docs/DEVELOPMENT.md)。

## 项目结构

```text
src/
  main.m                  应用界面、进程管理与内置自检
  Settings.h / .m         设置校验、日志筛选与持久化、更新地址白名单
  Projects.h / .m         项目清单与路径校验
  ProjectImport.h / .m    本机目录识别与批量导入
  UsageInterface.h / .m   只读 Code 用量控件与账号归属校验
  UsageHistory.h / .m     旧版历史实现留档，不参与编译
resources/                图标、界面插画、更新公钥与安装辅助脚本
scripts/                  构建、打包、DMG 与发布脚本
windows/                  Windows 原生实现
docs/                     规格、状态、使用说明与研究记录
AGENTS.md                 开发约束
```

## 文档

| 文档 | 内容 |
|------|------|
| [docs/SPEC.md](docs/SPEC.md) | 两端共同遵循的行为规格 |
| [docs/STATUS.md](docs/STATUS.md) | 版本、发布记录、验证结果与已知问题 |
| [docs/使用说明.txt](docs/使用说明.txt) | 日常使用说明 |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | 开发、签名与发布流程 |
| [docs/USAGE-RESEARCH.md](docs/USAGE-RESEARCH.md) | 本地用量读取的调查与边界 |
| [AGENTS.md](AGENTS.md) | 本地优先与安全约束 |

应用图标为项目自有图标；名称中的“Claude”仅用于说明用途，对外分发前请自行确认商标使用许可。
