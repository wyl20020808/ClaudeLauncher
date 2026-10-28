# Claude 启动器

非官方工具，与 Anthropic 无关联。原生 macOS 桌面账号启动器。源码版本：1.6（build 7，仅开发构建，未交付）；日常交付版本仍为 1.5（build 6）。Objective-C / AppKit。

统一管理 10 个 Claude 桌面账号：运行中显示绿色“打开”，已有目录未运行显示暖棕色“启动”，无目录显示蓝色“创建”。使用同一份官方 Claude.app，通过不同数据目录启动，不复制、修改或重签官方应用。

![界面](docs/assets/界面.png)

## 项目结构

```text
src/main.m                  macOS 应用实现与内置自检
windows/                    Windows 版（规划中，未实现）
docs/SPEC.md                跨平台行为规格
scripts/build.sh            构建、签名和自检
scripts/package.sh          打包已构建的应用
build/                      开发构建输出（Git 忽略）
releases/1.5/               当前已交付应用副本（Git 忽略）
docs/使用说明.txt            日常使用说明
docs/DEVELOPMENT.md          开发和验证流程
docs/STATUS.md              当前状态、已知问题和待办
docs/history/               历史实测证据，按原版本解读
docs/assets/                界面截图
AGENTS.md                   后续开发约束
```

## 运行与构建

日常使用可双击 `releases/1.5/Claude 启动器.app`，不需要安装 Python 或 Node.js。原交付的 `outputs/Claude 启动器.app` 仍保留原位置。辅助功能授权可能绑定路径和签名；已有授权请优先继续使用原交付路径。

开发需要 macOS、Xcode Command Line Tools 和 `/Applications/Claude.app` 或 `~/Applications/Claude.app`。在项目根目录执行：

```sh
/bin/zsh scripts/build.sh
/bin/zsh scripts/package.sh
```

开发应用位于 `build/Claude 启动器.app`，发布 ZIP 位于 `build/Claude启动器.zip`。构建不会替换原交付应用。支持 `SDKROOT` 指定 SDK，默认优先本机兼容的 MacOSX15.4.sdk。当前仅在本机 Apple Silicon 验证，未做 Apple 公证。

## 隔离范围

01 使用 Claude 默认目录；02 使用 Claude-Second；03～10 使用 Claude-Profile-03 至 Claude-Profile-10，均位于用户 Library/Application Support 下。已有目录直接复用。

1.6 起，02～10 启动时额外设置 `CLAUDE_CONFIG_DIR=<账号目录>/ClaudeCodeConfig`，Code 标签页的会话记录、设置、插件、记忆和 `.claude.json` 不再与 01 共用 `~/.claude`。01 保持默认 `~/.claude`。只对启动器新启动的实例生效。

独立数据目录不等于多台电脑，不保证隔离钥匙串、浏览器登录回调、日志目录（~/Library/Logs/Claude）、用户项目文件、后台服务或文件权限。需要“两台电脑”级别的隔离，请使用两个 macOS 用户账户。两个真实账号是否串号尚未验证。删除账号功能未实现，用户已暂停该需求。

当前主要待办是稳定应用签名并完成最新版本的最小化恢复验证，详见 [项目状态](docs/STATUS.md)。
