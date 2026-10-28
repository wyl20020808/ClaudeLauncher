# 开发与维护

## 技术结构

- `src/ClaudeLauncher.cs`：账号路径、进程发现、准入判断（打开/删除/登录保护/Code 状态）、登录保护持久化、窗口恢复及程序入口。
- `src/LauncherUI.cs`：WPF 四页界面、“···”菜单、对话框，以及后台检查后再核对的打开/删除/登录保护流程。
- `src/ProfileDeletion.cs`：目录范围校验、运行状态拦截、回收站操作和未完成删除标记。
- `src/Storage.cs`：存储元数据递归校验（链接、所有者、硬链接）及 `--self-test`。
- `installer/ClaudeLauncher.iss`、`scripts/setup.ps1`：Inno Setup 安装包。
- `src/Diagnostics.cs`：本地日志与账号进程退出观察。
- `src/Updates.cs`：版本号、更新地址、公钥验签、下载验证、替换与回退。
- `assets/update-public-key.xml`：客户端公开验签密钥，允许随源码分发。

程序使用 C# 5 / .NET Framework WPF。无 NuGet 依赖；构建脚本直接调用系统 `csc.exe`。源文件编码 UTF-8；PowerShell 脚本使用 UTF-8 BOM 以兼容 Windows PowerShell 5.1。

## 常用操作

```powershell
# 编译生产程序并执行自动检查
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1

# 更新安装集成验证：成功、失败回退、签名篡改
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-install.ps1

# 生成四个页面的离屏预览（artifacts/previews），不操作用户桌面
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/preview-ui.ps1

# 生成安装包 artifacts/setup/ClaudeLauncher-Setup-<版本>.exe（需 Inno Setup 6）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1

# 更新服务，只在发布电脑上使用；占用冲突会拒绝启动，不杀其他程序
powershell -NoProfile -ExecutionPolicy Bypass -File updates/start-server.ps1
```

自动检查源文件在 `tests/`。测试探针会启动短命后台进程，测试数据仅放入随机临时目录。更新集成测试保留临时证据。不要把测试退出码当成真实 Claude 稳定性验收。

## 发版流程

1. 修改 `src/Updates.cs` 的 AssemblyVersion、AssemblyFileVersion 和 Updates.Version，保持一致并递增；更新说明。
2. 构建并运行与改动相符的检查。检查版本比较用例仍然有效。
3. 在开发机做界面及真实账号的小范围验证，记录未验证边界。
4. 获得该次发布授权后运行 `scripts/publish-update.ps1`；默认读取 `artifacts/bin/ClaudeLauncher.exe`，写入本项目 `updates/public/`。
5. 用客户端核实 HTTPS 公网清单、签名、版本和文件哈希。
6. 更新客户 ZIP 与发行说明；旧版本保留，禁止混入账号数据或私钥。

更新服务器保留在 `updates/`，避免破坏当前隧道和运行服务。不要把 `updates/public/` 当开发输出目录；普通构建不能改写这里的已签名文件。

## 新电脑

解压项目包到可写目录。运行 `Build.cmd` 即可验证构建，不需要 Claude 已安装或发布私钥才能编译；实际使用启动器需要官方 Claude。Visual Studio 要求 .NET Framework 4.8 开发工具，项目附带 `.sln` 和两个传统 `.csproj`。

新电脑不自带 `updates/public/`、运行日志及旧备份目录。它们按需生成。若只开发界面或逻辑，不要启动服务器、修改签名身份或运行发布脚本。
