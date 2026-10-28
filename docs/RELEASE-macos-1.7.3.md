# macOS 1.7.3 发布交接

本轮仅更新 macOS（1.7.3 / build 10）。Windows 保持 1.7.2，不修改代码、不改版本、不重新构建或安装。需要 Windows 发布机只是因为现有自动更新的 RSA 私钥保存在该机器的 DPAPI 中；本机 Mac 只有应用代码签名证书，两者不能互相替代。

## 已准备的产物

- Release 标签：`macos-v1.7.3`，仓库 `wyl20020808/ClaudeLauncher`。
- macOS：`ClaudeLauncher-1.7.3.dmg`。
- 字节数：`1676413`。
- SHA-256：`55ecb85eb81d6deb2874413d49fad4a6cd210f6d4e11ad9e128450144a0682b0`。
- macOS 应用及 DMG 均使用 `ClaudeLauncher Local Signing`；应用与本机已安装 1.7.2 的签名要求相同。
- 原样带入 `v1.7.2` 的 `latest-windows.json`、`ClaudeLauncherSetup-1.7.2.exe`。原清单 RSA 签名、安装包大小和 SHA-256 已验证。Windows 安装包为 2793361 字节，SHA-256 为 `bea64d3d19916aa1e815ad8044f2914eb9d0c304700e179ea1ac9a53dc5e818a`。

## Windows 发布机剩余操作

1. 拉取仓库，检查 `macos-v1.7.3` 仍为草稿，并确认目标提交包含项目导入和 1.7.3 构建版本。下载草稿 DMG，复核以上大小与哈希。
2. 仅用现有 DPAPI 发布密钥在内存中签名 macOS 清单；密钥不得导出、打印、重建或上传。参考 `windows/scripts/publish-release.ps1` 的密钥读取、签名和验签逻辑，但不要直接运行联合发布模式：它会构建 Windows，且假设两端版本相同。
3. 生成 `latest-macos.json`：platform=`macos`，version=`1.7.3`，file=`ClaudeLauncher-1.7.3.dmg`，size=`1676413`，sha256 为上值，minimumOS=`12.0`。signature 为 RSA-3072 / SHA-256 PKCS#1 v1.5 签名的 Base64；签名原文如下，每行包括最后一行都以 LF 结尾，UTF-8 无 BOM：

   ```text
   macos
   1.7.3
   ClaudeLauncher-1.7.3.dmg
   1676413
   55ecb85eb81d6deb2874413d49fad4a6cd210f6d4e11ad9e128450144a0682b0
   ```

4. 用仓库原有公钥验签。将 DMG 复制到实际运行的自建更新服务 `public/`，把签名清单上传至草稿 Release。核对 Release 同时含新 Mac DMG／清单和原 Windows 1.7.2 安装包／清单。
5. 备份旧 `latest-macos.json`，原子切换自建服务的 macOS 清单，然后将草稿转正式并设为 Latest。不要改动自建服务的 `latest-windows.json`、`latest.json` 或 Windows 安装包。
6. 从自建服务和 GitHub `releases/latest/download/` 分别获取两端清单及对应包，验签、核对版本、大小、SHA-256。任一步失败恢复原 macOS 清单，并把新 Release 改回草稿。确保两端回退更新都可用。

签名清单缺失前保持草稿，不替换 Latest；否则 Mac 无法更新，还可能破坏 Windows 的 GitHub 回退。也不能只更新 GitHub 而让可用的自建服务一直返回旧版：现有 Mac 优先读取自建服务。

## 验证边界

Mac 通用构建、签名、DMG 校验和只读挂载检查通过；arm64／x86_64 六组自检通过；1.7.3 项目页离屏截图已核对。真实导入 GUI 和用户机器上的 1.7.2 → 1.7.3 自动更新尚未验证。正式发布后，在启动器“系统设置 → 检查更新”或菜单栏“检查更新…”中测试。不要修改或删除账号数据。
