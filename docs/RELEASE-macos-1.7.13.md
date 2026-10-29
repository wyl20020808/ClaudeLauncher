# macOS 1.7.13

实例卡片精简：去掉目录与 Code 配置标签，运行状态移到卡片左下角；目录和 Code 配置状态改在悬停提示中显示。

未设别名时，卡片与账号表显示本机 Claude Code 配置（`.claude.json`）中的用户名。只读取 `oauthAccount.displayName` 一个字段，纯本地读取：不联网、不启动 CLI、不访问钥匙串或 `config.json`，不保存、不记录日志、不导出。读不到时仍显示“账号 NN”。

固定签名证书不变；Windows 安装包保持原版本。
