# Windows 1.8.3

- 修复：启动器以管理员身份运行或系统关闭了 UAC 时，新建账号的 `ClaudeCodeConfig` 由 Windows 记为 Administrators 组所有，启动时被误报“存储项不属于当前用户”而无法打开。现在所有者校验同时接受当前用户与启动器自身新建文件时的默认所有者；其他所有者、链接、硬链接仍一律拒绝。

只做本地操作，不向 Claude／Anthropic 发请求，不读取登录凭据。安装包未做代码签名，首次运行可能出现 SmartScreen 提示。
