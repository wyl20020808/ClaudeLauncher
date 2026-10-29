# 开发与维护

## 技术结构

- `src/ClaudeLauncher.cs`：账号路径、进程发现、准入判断（打开/删除/登录保护/Code 状态）、登录保护持久化、窗口恢复及程序入口。
- `src/LauncherUI.cs`：WPF 四页界面（系统设置六分类与草稿／保存／撤销／恢复默认）、“···”菜单、对话框，以及后台检查后再核对的打开/删除/登录保护流程。
- `src/ProfileDeletion.cs`：目录范围校验、运行状态拦截、回收站操作和未完成删除标记。
- `src/Storage.cs`：存储元数据递归校验（链接、所有者、硬链接）及 `--self-test`。
- `installer/ClaudeLauncher.iss`、`scripts/setup.ps1`：Inno Setup 安装包。
- `scripts/publish-release.ps1`：两端一次发布（签名清单、上传自建服务与 GitHub Release、两来源复核）。
- `src/Diagnostics.cs`：本地日志与账号进程退出观察。
- `src/Updates.cs`：版本号、唯一更新来源（本项目 GitHub Releases）、集中 URL 白名单 `UrlAllowed`、开关未开启时的提前拒绝、清单验签、下载校验、Authenticode 签名者比对、运行 Setup；以及 1.2.0 兼容的 `--apply-update` 原地替换。新的联网入口必须经过 `UrlAllowed` 并在 `--self-test` 中加离线拒绝用例。
- `src/Settings.cs`：启动器自己的设置 `%LOCALAPPDATA%\Claude-MultiLauncher\settings.json`（SPEC 系统设置 [Windows] 的 11 项，逐项白名单校验、异常回落默认、原子替换写入、导出 version 1），上次页面 `last-page`，以及 HKCU Run 开机启动项 `AutoStart`（只接受正式安装路径）、账号本地别名 `AccountNames`（`account-names.json`，损坏时停止写入）与账号搜索匹配、启动器日志文件 `LogStore`（`launcher-log.json`，与 macOS 同格式，坏记录丢弃）、本地 Claude Code 用户名 `CodeUserName`（只取 `oauthAccount.displayName`；界面测试通过 `LauncherUI.CodeNameFiles` 注入合成文件，默认构造的测试实例不读取真实文件）。
- 界面测试与离屏预览用 `new LauncherUI(new LauncherSettings(), <临时路径>)`，不读写用户真实设置。
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

# 生成安装包 artifacts/setup/ClaudeLauncherSetup-<主.次.修订>.exe（需 Inno Setup 6）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup.ps1

# 更新服务，只在发布电脑上使用；占用冲突会拒绝启动，不杀其他程序
powershell -NoProfile -ExecutionPolicy Bypass -File updates/start-server.ps1
```

自动检查源文件在 `tests/`。测试探针会启动短命后台进程，测试数据仅放入随机临时目录。更新集成测试保留临时证据。不要把测试退出码当成真实 Claude 稳定性验收。

## 发版流程

1. 递增版本（两端各自递增）：修改 `src/Updates.cs` 的 AssemblyVersion、AssemblyFileVersion、`Version`（x.y.z.0）与 `DisplayVersion`（x.y.z）；更新说明。
2. 构建并运行与改动相符的检查（`build.ps1`、`--self-test`；改动 `--apply-update` 时再跑 `test-install.ps1`）。
3. 在开发机做界面及真实账号的小范围验证，记录未验证边界。
4. 两端同时发布时，macOS 端把 DMG 上传到草稿 Release `v<版本>` 并给出 size、sha256；只发布 Windows 时跳过。
5. 获得该次发布授权后运行 `scripts/publish-release.ps1`（两端同时发布时加 `-MacSize … -MacSha256 …`）（详见 `updates/维护说明.md`）；可先加 `-DryRun` 演练。
6. 脚本最后会从自建服务和 GitHub 各下载一次验签核对；之后可再用客户端“检查更新”确认（客户端只读 GitHub；自建服务仅供过渡期 1.7.3 及更早客户端，地址只在 `publish-release.ps1` 中）。

更新服务器保留在 `updates/`，避免破坏当前隧道和运行服务。不要把 `updates/public/` 当开发输出目录；普通构建不能改写这里的已签名文件。

## 新电脑

解压项目包到可写目录。运行 `Build.cmd` 即可验证构建，不需要 Claude 已安装或发布私钥才能编译；实际使用启动器需要官方 Claude。Visual Studio 要求 .NET Framework 4.8 开发工具，项目附带 `.sln` 和两个传统 `.csproj`。

新电脑不自带 `updates/public/`、运行日志及旧备份目录。它们按需生成。若只开发界面或逻辑，不要启动服务器、修改签名身份或运行发布脚本。
