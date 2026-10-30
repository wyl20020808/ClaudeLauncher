using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ClaudeLauncher {
    // 只读元数据（属性、所有者、硬链接数），从不打开文件内容。
    public static class Storage {
        public static readonly string[] Items = { "Cookies", "Cookies-journal", "Network", "Local Storage", "Session Storage", "IndexedDB",
            "Partitions", "Local State", "Preferences", "config.json", "Cache", "Code Cache", "Service Worker", "ClaudeCodeConfig" };

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoByHandle info);
        [StructLayout(LayoutKind.Sequential)]
        struct FileInfoByHandle {
            // FILETIME 为两个 DWORD（4 字节对齐），不能用 long，否则字段错位。
            public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        // 扩展长度路径：Claude 缓存常超过 260 字符，且客户电脑多未开启系统长路径支持，遍历一律用 \\?\ 前缀。
        public static string Extended(string path) {
            return path.StartsWith(@"\\") ? path : @"\\?\" + Path.GetFullPath(path);
        }
        public static string Display(string path) {
            return path.StartsWith(@"\\?\") ? path.Substring(4) : path;
        }
        public static int LinkCount(string path) {
            // FILE_READ_ATTRIBUTES，共享全部；BACKUP_SEMANTICS | OPEN_REPARSE_POINT 不跟随链接。
            using (SafeFileHandle handle = CreateFile(Extended(path), 0x80, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero)) {
                FileInfoByHandle info;
                if (handle.IsInvalid) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 2 || error == 3) return 0; // 检查期间被 Claude 删除的缓存文件。
                    throw new IOException("无法检查存储项：" + Display(path), Marshal.GetHRForLastWin32Error());
                }
                if (!GetFileInformationByHandle(handle, out info)) throw new IOException("无法检查存储项：" + Display(path), Marshal.GetHRForLastWin32Error());
                return (int)info.Links;
            }
        }
        static FileAttributes? Attributes(string path) {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        public static void Validate(int number, string roaming, string local) { Validate(number, roaming, local, true); }
        // recursive=false 只复查目录链与顶层存储项，用于后台递归检查完成后、启动前的快速复核。
        // 本进程新建文件的所有者：当前用户，或令牌默认所有者（已提权或关闭 UAC 的管理员为 Administrators 组）。
        public static bool OwnedByMe(SecurityIdentifier owner) {
            var me = WindowsIdentity.GetCurrent();
            return owner != null && (owner.Equals(me.User) || owner.Equals(me.Owner));
        }
        public static void Validate(int number, string roaming, string local, bool recursive) {
            foreach (string folder in ProfileDeletion.Candidates(number, roaming, local)) {
                for (string entry = folder; entry != null; entry = Path.GetDirectoryName(entry)) {
                    FileAttributes? attributes = Attributes(entry);
                    if (attributes == null) continue;
                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("账号目录路径含链接或重定向，已停止：" + entry);
                    if ((attributes.Value & FileAttributes.Directory) == 0) throw new IOException("账号目录路径不是文件夹，已停止：" + entry);
                }
                if (Attributes(folder) == null) continue;
                foreach (string name in Items) Check(Path.Combine(folder, name), recursive);
            }
        }
        // 逐项检查存储项及其内部：不跟随链接，检查期间消失的条目跳过，其余异常一律停止。
        static void Check(string root, bool recursive) {
            var pending = new Stack<string>(); pending.Push(Extended(root));
            while (pending.Count > 0) {
                string path = pending.Pop(), shown = Display(path);
                FileAttributes? attributes = Attributes(path);
                if (attributes == null) continue;
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("存储项含链接或重定向，已停止：" + shown);
                bool directory = (attributes.Value & FileAttributes.Directory) != 0;
                SecurityIdentifier owner;
                try {
                    FileSystemSecurity security = directory ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Owner) : File.GetAccessControl(path, AccessControlSections.Owner);
                    owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
                } catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
                catch (UnauthorizedAccessException) { throw new IOException("无法读取存储项所有者，已停止：" + shown); }
                if (!OwnedByMe(owner)) throw new IOException("存储项不属于当前用户，已停止：" + shown);
                if (!directory && LinkCount(path) > 1) throw new IOException("存储项是硬链接，已停止：" + shown);
                if (!directory || !recursive) continue;
                try { foreach (string entry in Directory.EnumerateFileSystemEntries(path)) pending.Push(entry); }
                catch (DirectoryNotFoundException) { }
                catch (UnauthorizedAccessException) { throw new IOException("无法读取存储目录，已停止：" + shown); }
            }
        }
    }

    public static class SelfTest {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool CreateHardLink(string link, string target, IntPtr security);
        static int checks;
        static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        static void Rejects(Action action, string message) {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; } catch (IOException) { rejected = true; } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, message);
        }
        public static int Entry() {
            AttachConsole(-1);
            try { Run(); Console.WriteLine("SELF-TEST PASS: " + checks + " checks"); return 0; }
            catch (Exception error) { Console.WriteLine("SELF-TEST FAIL: " + error.Message); return 1; }
        }
        public static void Run() {
            string root = Path.Combine(Path.GetTempPath(), "claude-selftest-" + Guid.NewGuid().ToString("N"));
            string roaming = Path.Combine(root, "Roaming"), local = Path.Combine(root, "Local");
            const string app = @"""C:\Program Files\WindowsApps\Claude_4.29.0.0_x64__pzs8sxrjxfjjc\app\claude.exe""";
            try {
                Directory.CreateDirectory(root);
                var me = WindowsIdentity.GetCurrent();
                Check(Storage.OwnedByMe(me.User) && Storage.OwnedByMe(me.Owner) && !Storage.OwnedByMe(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) && !Storage.OwnedByMe(null), "所有者判断错误");
                UpdateChecks(root);
                // 账号与 Code 配置映射
                var folders = Enumerable.Range(1, 10).Select(n => Core.Folder(n, roaming)).ToArray();
                Check(folders[0] == null && folders[1] == Path.Combine(roaming, "Claude-Second") && folders[9] == Path.Combine(roaming, "Claude-Profile-10"), "账号目录映射错误");
                Check(folders.Skip(1).Select(Core.CodeConfig).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 9, "Code 配置目录不唯一");
                Rejects(() => Core.Folder(11, roaming), "必须拒绝编号 11");
                // 参数解析
                for (int n = 1; n <= 10; n++) {
                    string arg = n == 1 ? "" : " --user-data-dir=\"" + folders[n - 1] + "\"";
                    Check(Core.NumberFromCommand(app + arg, roaming, local) == n, "等号形式映射错误");
                    if (n > 1) Check(Core.NumberFromCommand(app + " --user-data-dir \"" + folders[n - 1] + "\"", roaming, local) == n, "空格形式映射错误");
                    Check(Core.NumberFromCommand(app + " --type=renderer" + arg, roaming, local) == 0, "renderer 被计为账号");
                    Check(Core.NumberFromCommand(app + " --type=gpu-process" + arg, roaming, local) == 0, "GPU 进程被计为账号");
                }
                Check(Core.NumberFromCommand(app + @" --user-data-dir=C:\Other", roaming, local) == 0, "未管理目录被计为账号");
                Check(Core.IsDesktop(@"C:\Program Files\WindowsApps\Claude_4.29.0.0_x64__pzs8sxrjxfjjc\app\claude.exe"), "商店版主程序未识别");
                Check(!Core.IsDesktop(@"C:\Users\u\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\claude-code\2.1.0\x\claude.exe"), "Claude Code CLI 被识别为桌面版");
                Check(!Core.IsDesktop(@"C:\Users\u\.local\bin\claude.exe"), "CLI 被识别为桌面版");
                string two = " --user-data-dir=\"" + folders[1] + "\"";
                Rejects(() => Core.NumberFromCommand(app + two + two, roaming, local), "重复参数必须拒绝");
                Rejects(() => Core.NumberFromCommand(app + " --user-data-dir=Claude-Second", roaming, local), "相对路径必须拒绝");
                Rejects(() => Core.NumberFromCommand(app + " --user-data-dir", roaming, local), "缺值必须拒绝");
                Rejects(() => Core.NumberFromCommand(app + " --user-data-dir --foo", roaming, local), "缺值必须拒绝");
                Rejects(() => Core.NumberFromCommand(app + " --user-data-dir=", roaming, local), "空值必须拒绝");
                Rejects(() => Core.NumberFromCommand(null, roaming, local), "无法读取的进程必须拒绝");
                // 同账号主进程唯一性（托盘重启动前后复查）
                var trayRow = new Instance { Number = 2, ProcessId = 202, Executable = @"D:\Other\claude.exe" };
                var rows = new[] { new Instance { Number = 1, ProcessId = 101, Executable = @"C:\First\claude.exe" }, trayRow };
                Check(Core.Single(3, rows) == null, "未运行账号应返回空");
                Check(Core.Single(2, rows).Executable == trayRow.Executable, "托盘重启动须使用该 PID 自身的程序");
                Rejects(() => Core.Single(2, rows.Concat(new[] { new Instance { Number = 2, ProcessId = 203 } })), "同账号第二个主进程必须报错");
                // 账号显示与新建编号
                var none = new int[0];
                Func<int, bool> has = n => n == 1 || n == 4;
                Check(Core.Visible(4, rows, none, has) && Core.Visible(2, rows, none, has) && Core.Visible(6, rows, new[] { 6 }, has) && !Core.Visible(3, rows, none, has), "账号显示条件错误");
                Check(Core.NextFree(rows, none, has) == 3 && Core.NextFree(rows, new[] { 3 }, has) == 5, "新建编号应取最小未使用编号");
                Check(Core.NextFree(rows, none, n => true) == 0, "全部已用时应返回 0");
                // 保守 Code 状态：只认本次启动并仍存活的同一进程
                var started = new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc);
                var mine = new Instance { Number = 3, ProcessId = 303, Started = started };
                var record = new LaunchRecord { ProcessId = 303, Requested = started.AddSeconds(-1) };
                Check(Core.CodeState(1, new[] { new Instance { Number = 1, ProcessId = 1 } }, null) == "Code 默认", "01 应为 Code 默认");
                Check(Core.CodeState(3, rows, record) == "Code 待启动", "未运行应为 Code 待启动");
                Check(Core.CodeState(3, new[] { mine }, record) == "Code 已指定", "本次启动的进程应为 Code 已指定");
                Check(Core.CodeState(3, new[] { mine }, null) == "Code 未确认", "外部或旧进程应为 Code 未确认");
                Check(Core.CodeState(3, new[] { mine }, new LaunchRecord { ProcessId = 303, Requested = started.AddSeconds(1) }) == "Code 未确认", "早于请求的进程不能算已指定");
                Check(Core.CodeState(3, new[] { mine }, new LaunchRecord { ProcessId = 304, Requested = record.Requested }) == "Code 未确认", "PID 变化不能沿用已指定");
                Check(Core.CodeState(3, new[] { mine, new Instance { Number = 3, ProcessId = 305, Started = started } }, record) == "Code 未确认", "重复主进程不能算已指定");
                // 删除准入：01、运行中、启动/检查中、登录保护中均不可删
                Check(Core.DeleteBlocker(1, new Instance[0], false, 0) != null, "01 必须不可删");
                Check(Core.DeleteBlocker(2, rows, false, 0) != null, "运行中账号必须不可删");
                Check(Core.DeleteBlocker(3, rows, true, 0) != null, "启动或检查中账号必须不可删");
                Check(Core.DeleteBlocker(3, rows, false, 3) != null, "登录保护中账号必须不可删");
                Check(Core.DeleteBlocker(3, rows, false, 4) == null, "未运行账号应允许删除");
                // 登录保护准入与保护期间打开
                var unmanaged = new Instance { Number = 0, ProcessId = 900 };
                Check(Core.LoginBlocker(2, new[] { trayRow }, none) == null, "仅目标账号运行时应允许进入保护");
                Check(Core.LoginBlocker(3, new[] { trayRow }, none) != null, "其他账号运行时必须阻止");
                Check(Core.LoginBlocker(3, new[] { unmanaged }, none) != null, "未管理实例必须阻止");
                Check(Core.LoginBlocker(2, new[] { trayRow, new Instance { Number = 2, ProcessId = 203 } }, none) != null, "目标重复主进程必须阻止");
                Check(Core.LoginBlocker(3, new Instance[0], new[] { 5 }) != null && Core.LoginBlocker(3, new Instance[0], new[] { 3 }) == null, "其他账号进行中必须阻止");
                Check(Core.OpeningBlocker(4, 0, new Instance[0], 3, none) != null, "保护期间不能打开其他账号");
                Check(Core.OpeningBlocker(3, 0, new Instance[0], 3, new[] { 3 }) == null, "保护期间应能打开目标账号");
                Check(Core.OpeningBlocker(3, 0, new[] { unmanaged }, 3, none) != null, "保护期间出现其他实例必须阻止");
                // 检查期间 PID 退出或被替换：不自动重启、不切换
                Check(Core.OpeningBlocker(2, 202, rows, 0, none) == null, "PID 未变应允许激活");
                Check(Core.OpeningBlocker(2, 201, rows, 0, none) != null, "PID 被替换必须阻止");
                Check(Core.OpeningBlocker(3, 303, rows, 0, none) != null, "PID 已退出必须阻止");
                // 登录保护持久化
                Check(LoginProtection.Read(local) == 0, "初始不应有登录保护");
                LoginProtection.Save(local, 4);
                Check(LoginProtection.Read(local) == 4, "登录保护未持久化");
                LoginProtection.Save(local, 0);
                Check(LoginProtection.Read(local) == 0, "结束保护未清除");
                // 启动参数与环境变量
                var first = Core.PrepareLaunch(1, "claude.exe", roaming);
                Check(first.Arguments == "" && !first.UseShellExecute && !first.EnvironmentVariables.ContainsKey("CLAUDE_CONFIG_DIR"), "01 不应传覆盖参数或配置变量");
                var second = Core.PrepareLaunch(2, "claude.exe", roaming);
                Check(second.EnvironmentVariables["CLAUDE_CONFIG_DIR"] == Core.CodeConfig(folders[1]) && Directory.Exists(Core.CodeConfig(folders[1])), "02 的 CLAUDE_CONFIG_DIR 错误");
                // 路径与存储校验
                string folder = folders[1];
                File.WriteAllText(Path.Combine(folder, "Preferences"), "{}");
                Directory.CreateDirectory(Path.Combine(folder, "Network"));
                Storage.Validate(2, roaming, local);
                string outside = Path.Combine(root, "outside.txt");
                File.WriteAllText(outside, "x");
                File.Delete(Path.Combine(folder, "Preferences"));
                Check(CreateHardLink(Path.Combine(folder, "Preferences"), outside, IntPtr.Zero), "无法创建硬链接夹具");
                Rejects(() => Storage.Validate(2, roaming, local), "硬链接必须拒绝");
                File.Delete(Path.Combine(folder, "Preferences"));
                Directory.Delete(Path.Combine(folder, "Network"));
                string target = Path.Combine(root, "target");
                Directory.CreateDirectory(target);
                Junction(Path.Combine(folder, "Network"), target);
                Rejects(() => Storage.Validate(2, roaming, local), "链接存储项必须拒绝");
                Directory.Delete(Path.Combine(folder, "Network"));
                Storage.Validate(2, roaming, local);
                // 递归：存储项内部的深层链接、硬链接同样拒绝；非递归复核只看顶层
                string deep = Path.Combine(folder, @"Network\a\b");
                Directory.CreateDirectory(deep);
                File.WriteAllText(Path.Combine(deep, "ok.txt"), "x");
                Storage.Validate(2, roaming, local);
                Check(CreateHardLink(Path.Combine(deep, "linked.txt"), outside, IntPtr.Zero), "无法创建深层硬链接夹具");
                Rejects(() => Storage.Validate(2, roaming, local), "深层硬链接必须拒绝");
                Storage.Validate(2, roaming, local, false);
                File.Delete(Path.Combine(deep, "linked.txt"));
                Junction(Path.Combine(deep, "junction"), target);
                Rejects(() => Storage.Validate(2, roaming, local), "深层目录链接必须拒绝");
                Directory.Delete(Path.Combine(deep, "junction"));
                string code = Path.Combine(Core.CodeConfig(folder), "projects");
                Directory.CreateDirectory(Path.GetDirectoryName(code));
                Junction(code, target);
                Rejects(() => Storage.Validate(2, roaming, local), "深层 Code 存储链接必须拒绝");
                Directory.Delete(code);
                Storage.Validate(2, roaming, local);
                // 长路径（超过 260 字符，Claude 缓存常见）：正常文件通过，深层硬链接仍拒绝
                string longDir = Path.Combine(folder, "Cache", new string('d', 120), new string('e', 120), new string('f', 120));
                Directory.CreateDirectory(Storage.Extended(longDir));
                string longFile = Storage.Extended(Path.Combine(longDir, "data_0"));
                File.WriteAllText(longFile, "x");
                Check(Storage.Display(longFile).Length > 300, "长路径夹具不够长");
                Storage.Validate(2, roaming, local);
                File.Delete(longFile);
                Check(CreateHardLink(longFile, outside, IntPtr.Zero), "无法创建长路径硬链接夹具");
                Rejects(() => Storage.Validate(2, roaming, local), "长路径下的硬链接必须拒绝");
                File.Delete(longFile);
                Storage.Validate(2, roaming, local);
                Junction(folders[2], target);
                Rejects(() => Storage.Validate(3, roaming, local), "链接账号目录必须拒绝");
                Directory.Delete(folders[2]);
            } finally { Cleanup(root); }
        }
        // 更新协议：用一次性 RSA-3072 密钥签名，覆盖验签、篡改拒绝、macOS 清单、版本比较、系统版本、跳转白名单、签名者规则。
        static void UpdateChecks(string root) {
            using (var probe = new System.Security.Cryptography.RSACryptoServiceProvider()) {
                probe.PersistKeyInCsp = false; probe.FromXmlString(Updates.PublicKey());
                Check(probe.KeySize == 3072, "内置更新公钥应为 RSA-3072");
            }
            using (var rsa = new System.Security.Cryptography.RSACryptoServiceProvider(3072)) {
                rsa.PersistKeyInCsp = false;
                string key = rsa.ToXmlString(false);
                Func<Release, string> sign = r => {
                    r.signature = Convert.ToBase64String(rsa.SignData(Updates.Payload(r), System.Security.Cryptography.CryptoConfig.MapNameToOID("SHA256")));
                    return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(r);
                };
                Func<Release> good = () => new Release { platform = "windows", version = "1.8.0", file = "ClaudeLauncherSetup-1.8.0.exe", size = 2048, sha256 = new string('a', 64), minimumOS = "10.0.19041" };
                string json = sign(good());
                Check(Updates.Verify(json, key).version == "1.8.0", "有效清单被拒绝");
                Check(Updates.Payload(good()).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("windows\n1.8.0\nClaudeLauncherSetup-1.8.0.exe\n2048\n" + new string('a', 64) + "\n")), "签名原文格式错误");
                Rejects(() => Updates.Verify(json.Replace(new string('a', 64), new string('b', 64)), key), "篡改哈希必须被签名拒绝");
                Rejects(() => Updates.Verify(json.Replace("2048", "4096"), key), "篡改大小必须被签名拒绝");
                Rejects(() => Updates.Verify(json, new System.Security.Cryptography.RSACryptoServiceProvider(3072) { PersistKeyInCsp = false }.ToXmlString(false)), "其他密钥签名必须拒绝");
                Rejects(() => Updates.Verify(json, key, "macos"), "Windows 清单不能当 macOS 清单");
                Rejects(() => Updates.Verify(new string(' ', 16385), key), "超大清单必须拒绝");
                Rejects(() => Updates.Verify("null", key), "空清单必须拒绝");
                Rejects(() => Updates.Verify(json.Replace("\"signature\":\"", "\"signature\":\"!"), key), "非 Base64 签名必须拒绝");
                var changes = new Action<Release>[] {
                    r => r.platform = "macos", r => r.file = "evil.exe", r => r.file = "ClaudeLauncherSetup-1.9.0.exe", r => r.size = Updates.SizeLimit + 1, r => r.size = 10,
                    r => r.version = "1.8", r => r.version = "1.8.0.0", r => r.sha256 = "ABC", r => r.sha256 = new string('A', 64), r => r.minimumOS = "ten" };
                foreach (var change in changes) {
                    Release bad = good(); change(bad);
                    Rejects(() => Updates.Verify(sign(bad), key), "格式不符的清单必须拒绝（即使签名正确）");
                }
                var mac = new Release { platform = "macos", version = "1.8.0", file = "ClaudeLauncher-1.8.0.dmg", size = 4096, sha256 = new string('c', 64), minimumOS = "12.0" };
                Check(Updates.Verify(sign(mac), key, "macos").file == "ClaudeLauncher-1.8.0.dmg", "macOS 清单验签失败");
                Check(System.Text.Encoding.UTF8.GetString(Updates.Payload(mac)).StartsWith("macos\n1.8.0\nClaudeLauncher-1.8.0.dmg\n4096\n"), "macOS 签名原文错误");
                var oldName = new Release { platform = "macos", version = "1.8.0", file = "Claude多开启动器-1.8.0.dmg", size = 4096, sha256 = new string('c', 64), minimumOS = "12.0" };
                Rejects(() => Updates.Verify(sign(oldName), key, "macos"), "旧的中文 DMG 文件名必须拒绝");
            }
            Check(Updates.Compare("1.10.0", "1.9.9") > 0 && Updates.Compare("1.7.0", "1.7") == 0 && Updates.Compare("1.6.9", "1.7.0") < 0, "版本比较错误");
            // 相对当前版本构造“更高/更低”，版本号递增后无需改测试。
            int[] current = Updates.DisplayVersion.Split('.').Select(Int32.Parse).ToArray();
            string higher = current[0] + "." + current[1] + "." + (current[2] + 1);
            Check(!Updates.IsNewer(new Release { version = Updates.DisplayVersion }) && !Updates.IsNewer(new Release { version = "1.6.0" }) && Updates.IsNewer(new Release { version = higher }), "只升级、不降级");
            Check(Updates.OsSupported("10.0.19041", new Version(10, 0, 26200)) && !Updates.OsSupported("10.0.19041", new Version(10, 0, 18363)), "系统版本比较错误");
            // SPEC 网络边界：集中 URL 白名单。首次请求只允许本项目 latest/download 下的清单与 Setup；跳转只允许同仓库 Release 下载路径与 GitHub 附件主机。
            string base_ = Updates.GitHubBase, tagged = "https://github.com/wyl20020808/ClaudeLauncher/releases/download/windows-v1.8.0/";
            foreach (string ok in new[] { base_ + "latest-windows.json", base_ + "ClaudeLauncherSetup-1.8.0.exe" })
                Check(Updates.UrlAllowed(new Uri(ok), true) && Updates.UrlAllowed(new Uri(ok), false), "本项目清单与 Setup 地址应允许：" + ok);
            foreach (string ok in new[] { tagged + "latest-windows.json", tagged + "ClaudeLauncherSetup-1.8.0.exe",
                "https://objects.githubusercontent.com/github-production-release-asset/1?X-Amz-Signature=a", "https://release-assets.githubusercontent.com/github-production-release-asset/1?sp=r" })
                Check(Updates.UrlAllowed(new Uri(ok), false), "允许的跳转被拒绝：" + ok);
            Check(!Updates.UrlAllowed(new Uri(tagged + "latest-windows.json"), true) && !Updates.UrlAllowed(new Uri("https://objects.githubusercontent.com/x"), true), "附件主机与标签路径不能作为首次请求");
            string[] denied = {
                "http://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-windows.json",
                "https://github.com:8443/wyl20020808/ClaudeLauncher/releases/latest/download/latest-windows.json",
                "https://user:pass@github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-windows.json",
                "https://github.com/other/ClaudeLauncher/releases/latest/download/latest-windows.json",
                "https://github.com/wyl20020808/ClaudeLauncher/archive/main.zip",
                "https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json",
                "https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/ClaudeLauncherSetup-1.8.exe",
                "https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-windows.json?x=1",
                "https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/../../../../evil/latest-windows.json",
                "https://github.com.evil.example/wyl20020808/ClaudeLauncher/releases/latest/download/latest-windows.json",
                "https://evilgithub.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-windows.json",
                "https://objects.githubusercontent.com.evil.example/x", "https://objects.githubusercontent.com:8443/x", "http://release-assets.githubusercontent.com/x",
                "https://www.u4139693.nyat.app:53569/claude-launcher/latest-windows.json",
                "https://claude.ai/latest-windows.json", "https://api.anthropic.com/latest-windows.json", "https://anthropic.com/x" };
            foreach (string bad in denied) Check(!Updates.UrlAllowed(new Uri(bad), true) && !Updates.UrlAllowed(new Uri(bad), false), "非白名单地址必须拒绝：" + bad);
            Check(!Updates.UrlAllowed(null, true) && !Updates.UrlAllowed(new Uri("/relative", UriKind.Relative), false), "空或相对地址必须拒绝");
            // 离线拒绝：未开启时检查与下载在创建任何请求前拒绝。
            int before = Updates.Requests;
            Rejects(() => Updates.Check(false), "未开启更新时检查必须拒绝");
            Rejects(() => Updates.Download(new Release { platform = "windows", version = "1.8.0", file = "ClaudeLauncherSetup-1.8.0.exe", size = 2048, sha256 = new string('a', 64), minimumOS = "10.0.19041", signature = "AA==" }, false, null, null), "未开启更新时下载必须拒绝");
            Check(Updates.Requests == before, "未开启更新时不得创建网络请求");
            // 设置：默认关闭；往返；损坏、未知版本、类型不符回落关闭；保存不留临时文件。
            string settingsFile = Path.Combine(root, "settings", "settings.json");
            Check(!LauncherSettings.Load(settingsFile).AllowUpdates, "缺少设置文件时更新应默认关闭");
            new LauncherSettings { AllowUpdates = true }.Save(settingsFile);
            Check(LauncherSettings.Load(settingsFile).AllowUpdates, "设置保存后读取不一致");
            new LauncherSettings { AllowUpdates = false }.Save(settingsFile);
            Check(!LauncherSettings.Load(settingsFile).AllowUpdates && Directory.GetFiles(Path.GetDirectoryName(settingsFile)).Length == 1, "覆盖保存失败或残留临时文件");
            foreach (string broken in new[] { "{", "null", "[]", "{\"version\":2,\"allowUpdates\":true}", "{\"allowUpdates\":true}", "{\"version\":1,\"allowUpdates\":\"true\"}", "{\"version\":\"1\",\"allowUpdates\":true}" }) {
                File.WriteAllText(settingsFile, broken);
                Check(!LauncherSettings.Load(settingsFile).AllowUpdates, "异常设置必须回落为关闭：" + broken);
            }
            // SPEC 系统设置 [Windows]：默认值、逐项校验（异常项只回落该项）、全部字段往返、导出不含账号数据、上次页面白名单。
            var defaults = new LauncherSettings();
            Check(defaults.CloseToBackground && defaults.ShowTray && !defaults.RememberPage && defaults.StartPage == "instances" && defaults.RefreshInterval == 3 &&
                defaults.RestoreMinimized && defaults.ShowArtwork && defaults.LogLimit == 500 && defaults.LocalNotifications && !defaults.NotificationSound && !defaults.AllowUpdates, "设置默认值与 SPEC 不符");
            File.WriteAllText(settingsFile, "{\"version\":1,\"refreshInterval\":0,\"logLimit\":999999,\"startPage\":\"C:\\\\Windows\",\"showTray\":\"yes\",\"closeToBackground\":false,\"unknown\":true}");
            var mixed = LauncherSettings.Load(settingsFile);
            Check(mixed.RefreshInterval == 3 && mixed.LogLimit == 500 && mixed.StartPage == "instances" && mixed.ShowTray && !mixed.CloseToBackground && mixed.LoadError == null, "单项异常只应回落该项");
            var changed = new LauncherSettings { CloseToBackground = false, ShowTray = false, RememberPage = true, StartPage = "logs", RefreshInterval = 5, RestoreMinimized = false,
                ShowArtwork = false, LogLimit = 100, LocalNotifications = false, NotificationSound = true, AllowUpdates = true };
            changed.Save(settingsFile);
            Check(LauncherSettings.Load(settingsFile).SameAs(changed) && !changed.SameAs(defaults) && changed.Clone().SameAs(changed), "设置往返或比较错误");
            string exported = changed.ExportJson();
            Check(exported.StartsWith("{\"version\":1,\"settings\":{") && !exported.Contains("accountLastUsed") && !exported.Contains("accountNames") && !exported.Contains("Claude-") && !exported.Contains("lastPage"), "导出设置格式错误或含账号数据");
            string lastPage = Path.Combine(root, "settings", "last-page");
            Check(LauncherSettings.ReadLastPage(lastPage) == null, "缺少上次页面应为空");
            LauncherSettings.WriteLastPage(lastPage, "accounts");
            Check(LauncherSettings.ReadLastPage(lastPage) == "accounts", "上次页面往返错误");
            File.WriteAllText(lastPage, @"..\..\evil");
            Check(LauncherSettings.ReadLastPage(lastPage) == null, "未知上次页面必须忽略");
            Rejects(() => LauncherSettings.WriteLastPage(lastPage, "evil"), "不得写入未知页面");
            // 账号别名：≤24 字、去首尾空白、拒绝控制字符；往返；损坏或未知版本抛出且保留原文件；无效条目跳过；搜索字段。
            Check(AccountNames.Validate("  工作账号  ") == "工作账号" && AccountNames.Validate("   ") == "" && AccountNames.Validate("a\nb") == null &&
                AccountNames.Validate(new string('长', 25)) == null && AccountNames.Validate(new string('长', 24)) != null && AccountNames.Validate(null) == null, "别名校验错误");
            string namesFile = Path.Combine(root, "settings", "account-names.json");
            Check(AccountNames.Load(namesFile).Count == 0, "缺少别名文件应为空");
            AccountNames.Save(namesFile, new Dictionary<int, string> { { 2, "工作" }, { 10, "测试" } });
            var names = AccountNames.Load(namesFile);
            Check(names.Count == 2 && names[2] == "工作" && names[10] == "测试", "别名往返错误");
            Rejects(() => AccountNames.Save(namesFile, new Dictionary<int, string> { { 11, "越界" } }), "越界编号别名必须拒绝");
            File.WriteAllText(namesFile, "{\"version\":1,\"names\":{\"2\":\"好\",\"0\":\"x\",\"11\":\"x\",\"02\":\"x\",\"3\":\"a\\nb\",\"4\":5}}");
            names = AccountNames.Load(namesFile);
            Check(names.Count == 1 && names[2] == "好", "无效别名条目应跳过");
            foreach (string broken in new[] { "{", "{\"version\":2,\"names\":{}}", "{\"version\":1}", "[]" }) {
                File.WriteAllText(namesFile, broken);
                bool threw = false; try { AccountNames.Load(namesFile); } catch (InvalidDataException) { threw = true; }
                Check(threw && File.ReadAllText(namesFile) == broken, "损坏别名文件必须报错且保留原文：" + broken);
            }
            Check(AccountNames.Matches(2, "工作", @"C:\x\Claude-Second", "工作") && AccountNames.Matches(2, null, "", "账号 02") && AccountNames.Matches(2, null, "", "2") &&
                AccountNames.Matches(3, null, @"C:\x\Claude-Profile-03", "profile-03") && AccountNames.Matches(3, null, "", "  ") && !AccountNames.Matches(3, "家", @"C:\x\Claude-Profile-03", "工作"), "账号搜索匹配错误");
            // 默认用户名：只取 oauthAccount.displayName（合成文件，不读真实 .claude.json）；校验与别名相同；坏文件、缺失、目录、类型不符均为空。
            string nameFile = Path.Combine(root, "settings", ".claude.json");
            File.WriteAllText(nameFile, "{\"oauthAccount\":{\"displayName\":\"  Eric \",\"emailAddress\":\"x@example.invalid\"},\"other\":1}");
            Check(CodeUserName.Read(nameFile) == "Eric", "本地用户名应读取并去除空白");
            string firstStamp = CodeUserName.Stamp(nameFile);
            foreach (string bad in new[] { "{\"oauthAccount\":{\"displayName\":\"a\\nb\"}}", "not json", "{\"oauthAccount\":{\"displayName\":5}}", "{\"oauthAccount\":\"x\"}", "{}", "[]",
                "{\"oauthAccount\":{\"displayName\":\"" + new string('长', 25) + "\"}}", "{\"oauthAccount\":{\"displayName\":\"   \"}}" }) {
                File.WriteAllText(nameFile, bad);
                Check(CodeUserName.Read(nameFile) == null, "无效用户名文件必须回落：" + bad);
            }
            Check(CodeUserName.Stamp(nameFile) != firstStamp, "文件变化时修改戳应变化");
            Check(CodeUserName.Read(Path.Combine(root, "missing.json")) == null && CodeUserName.Stamp(Path.Combine(root, "missing.json")) == null && CodeUserName.Read(root) == null && CodeUserName.Stamp(root) == null, "缺失文件或目录必须回落");
            string[] firstCandidates = CodeUserName.Candidates(1, @"C:\Users\x", @"C:\Users\x\AppData\Roaming", @"C:\Users\x\AppData\Local");
            string[] secondCandidates = CodeUserName.Candidates(2, @"C:\Users\x", @"C:\Users\x\AppData\Roaming", @"C:\Users\x\AppData\Local");
            Check(firstCandidates.SequenceEqual(new[] { @"C:\Users\x\.claude.json" }) &&
                secondCandidates.SequenceEqual(new[] { @"C:\Users\x\AppData\Roaming\Claude-Second\ClaudeCodeConfig\.claude.json", @"C:\Users\x\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude-Second\ClaudeCodeConfig\.claude.json" }), "用户名候选路径错误");
            // 日志文件：与 macOS 同格式往返、最新在前；整体损坏为空；单条类型／级别／实例不符丢弃；数字页码规则。
            string logFile = Path.Combine(root, "settings", "launcher-log.json");
            Check(LogStore.Load(logFile).Count == 0, "缺少日志文件应为空");
            var stamp = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Local);
            LogStore.Save(logFile, new[] { new LogEntry { At = stamp.AddMinutes(1), Level = "错误", Instance = "Claude 2", Text = "账号 02 启动失败" }, new LogEntry { At = stamp, Level = "信息", Instance = "系统", Text = "初始化" } });
            var loaded = LogStore.Load(logFile);
            Check(loaded.Count == 2 && loaded[0].Text == "账号 02 启动失败" && loaded[0].Instance == "Claude 2" && loaded[0].At == stamp.AddMinutes(1) && loaded[0].Time == "2026-10-29 12:01:00" && loaded[1].Level == "信息", "日志文件往返错误");
            Check(File.ReadAllText(logFile).StartsWith("[{\"date\":"), "日志文件格式应与 macOS 一致（JSON 数组）");
            File.WriteAllText(logFile, "[{\"date\":1,\"level\":\"信息\",\"instance\":\"系统\",\"text\":\"好\"},{\"date\":\"1\",\"level\":\"信息\",\"instance\":\"系统\",\"text\":\"x\"},{\"date\":1,\"level\":\"调试\",\"instance\":\"系统\",\"text\":\"x\"},{\"date\":1,\"level\":\"信息\",\"instance\":\"Claude 11\",\"text\":\"x\"},{\"date\":1,\"level\":\"信息\",\"instance\":\"系统\"},{\"date\":-5,\"level\":\"信息\",\"instance\":\"系统\",\"text\":\"x\"},5,null]");
            Check(LogStore.Load(logFile).Count == 1, "格式不符的日志记录必须丢弃");
            foreach (string broken in new[] { "{", "{}", "null", "\"x\"" }) { File.WriteAllText(logFile, broken); Check(LogStore.Load(logFile).Count == 0, "损坏日志文件应读为空：" + broken); }
            Check(LauncherUI.PageNumbers(1, 0).SequenceEqual(new[] { 0 }) && LauncherUI.PageNumbers(10, 0).SequenceEqual(new[] { 0, 1, 9 }) && LauncherUI.PageNumbers(10, 5).SequenceEqual(new[] { 0, 4, 5, 6, 9 }) && LauncherUI.PageNumbers(3, 1).SequenceEqual(new[] { 0, 1, 2 }), "数字页码规则错误");
            // 本地界面用量：只用合成节点树，不访问真实 Claude。
            var fh = UsageInterface.ButtonMetric("用量：5 小时限额的 52%周四01:10 重置");
            Check(fh != null && fh.Item1 == "fh" && fh.Item2 == 52 && fh.Item3 == "周四01:10 重置", "五小时按钮解析错误");
            var sd = UsageInterface.ButtonMetric("用量：每周 · 所有模型：11%周三19:00 重置");
            Check(sd != null && sd.Item1 == "sd" && sd.Item2 == 11, "周按钮解析错误");
            Check(UsageInterface.ButtonMetric("Usage: 7.5% of the 5-hour limit Resets 3pm").Item2 == 7.5, "英文五小时按钮解析错误");
            Check(UsageInterface.ButtonMetric("用量：5 小时限额的 101%") == null && UsageInterface.ButtonMetric("上下文 52%") == null && UsageInterface.ButtonMetric("用量：每周 · Opus：5%") == null && UsageInterface.ButtonMetric(null) == null, "无效百分比、上下文与模型专属周限额必须排除");
            Check(UsageInterface.CodeUrl("https://claude.ai/epitaxy") && UsageInterface.CodeUrl("https://claude.ai/epitaxy/local_x") &&
                !UsageInterface.CodeUrl("https://claude.ai/epitaxyx") && !UsageInterface.CodeUrl("https://claude.ai/epitaxy/") && !UsageInterface.CodeUrl("https://evil.test/epitaxy") &&
                !UsageInterface.CodeUrl("https://claude.ai:8443/epitaxy") && !UsageInterface.CodeUrl("https://claude.ai/epitaxy?x=1") && !UsageInterface.CodeUrl("https://claude.ai/epitaxy#a") &&
                !UsageInterface.CodeUrl("http://claude.ai/epitaxy") && !UsageInterface.CodeUrl("https://u@claude.ai/epitaxy") && !UsageInterface.CodeUrl("https://claude.ai/chat/x"), "Code 页面地址校验错误");
            Func<UsageNode[], UsageNode> group = children => new UsageNode { Children = children.ToList() };
            Func<string, UsageNode[], UsageNode> page = (url, children) => new UsageNode { Type = "Document", Url = url, Children = children.ToList() };
            Func<string, UsageNode> button = name => new UsageNode { Type = "Button", Name = name, Expandable = true };
            const string code = "https://claude.ai/epitaxy/local_synthetic";
            var shell = new UsageNode { Type = "Pane", Children = { new UsageNode { Type = "Document" }, group(new[] { page(code, new[] { group(new[] { button("用量：每周 · 所有模型：11%周三19:00 重置") }) }) }) } };
            UsageRecord read = UsageInterface.ReadSynthetic(shell, 5);
            Check(read.Status == UsageInterface.Observed && read.Weekly.Percent == 11 && read.FiveHour == null, "合成树应读到周值");
            Check(UsageInterface.ReadSynthetic(page("https://evil.test/epitaxy/x", new[] { button("用量：5 小时限额的 52%") }), 5).FiveHour == null, "非官方页面必须拒绝");
            Check(UsageInterface.ReadSynthetic(group(new[] { button("用量：5 小时限额的 52%") }), 5).Status == "未观察到 Code 用量页面", "Code 页面以外的按钮不得采用");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { new UsageNode { Type = "Text", Name = "用量：5 小时限额的 52%" }, new UsageNode { Type = "Button", Name = "用量：5 小时限额的 52%" } }), 5).Status == "未观察到 Code 用量控件", "正文或不可展开按钮不得提供额度");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { new UsageNode { Type = "Text", Children = { button("用量：5 小时限额的 52%") } } }), 5).FiveHour == null, "正文分支不得进入");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { page(code, new[] { button("用量：5 小时限额的 52%") }) }), 5).FiveHour == null, "嵌套网页不得进入");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { button("用量：5 小时限额的 52%"), button("用量：5 小时限额的 53%") }), 5).Status == "多个用量界面，待核对", "多个用量按钮不得采用");
            // 弹窗面板（D3，按 Windows 4.29 实测结构）：链接核实后读取两项；与按钮不一致、重复、缺链接、模型专属周限额均不采用。
            Func<string, double, UsageNode> bar = (name, value) => new UsageNode { Type = "ProgressBar", Name = name, Range = value };
            Func<string, UsageNode> text = value => new UsageNode { Type = "Text", Name = value };
            Func<string, UsageNode[], UsageNode> popup = (link, rows) => new UsageNode { Type = "Window", Name = "用量", Children = { group(new[] { text("用量"), new UsageNode { Type = "Hyperlink", Name = "查看全部：设置中的用量", Url = link }, group(rows) }) } };
            UsageNode[] limits = { group(new[] { text("5 小时限额"), text("15:20 重置") }), bar("5 小时限额", 71), group(new[] { text("每周 · 所有模型"), text("周二19:00 重置") }), bar("每周 · 所有模型", 11) };
            UsageNode weekButton = button("用量：每周 · 所有模型：11%周二19:00 重置");
            read = UsageInterface.ReadSynthetic(page(code, new[] { group(new[] { weekButton, popup("https://claude.ai/settings/usage", limits) }) }), 5);
            Check(read.Status == UsageInterface.Observed && read.FiveHour.Percent == 71 && read.FiveHour.Reset == "15:20 重置" && read.Weekly.Percent == 11 && read.Weekly.Reset == "周二19:00 重置", "面板应读到五小时与每周");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { button("用量：每周 · 所有模型：12%周二19:00 重置"), popup("https://claude.ai/settings/usage", limits) }), 5).Status == "界面数据待核对", "面板与按钮不一致必须拒绝");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://evil.test/settings/usage", limits) }), 5).FiveHour == null, "没有官方用量链接的弹窗不得读取");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://claude.ai/settings/usage", limits.Concat(new[] { bar("5 小时限额", 70) }).ToArray()) }), 5).Status == "界面数据待核对", "重复指标必须拒绝");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://claude.ai/settings/usage", limits), popup("https://claude.ai/settings/usage", limits) }), 5).Status == "界面数据待核对", "多个面板必须拒绝");
            read = UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://claude.ai/settings/usage", new[] { bar("每周 · Opus", 40), bar("每周 · 所有模型", 11), bar("上下文", 52) }) }), 5);
            Check(read.Status == UsageInterface.Observed && read.Weekly.Percent == 11 && read.FiveHour == null, "模型专属周限额与上下文不得采用");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { popup("https://claude.ai/settings/usage", limits) }), 5).Status == "未观察到 Code 用量控件", "没有用量按钮时不采用面板");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://claude.ai/settings/usage", new[] { bar("5 小时限额", 101), bar("每周 · 所有模型", 11) }) }), 5).Status == "界面数据待核对", "越界进度值必须拒绝");
            var watch = new PopupWatch();
            Check(watch.Accept(new[] { 1, 2 }, true) && !watch.Accept(new[] { 1, 2 }, true) && !watch.Accept(new[] { 1, 2 }, false) && watch.Accept(new[] { 1, 2 }, true) && !watch.Accept(new[] { 9 }, false), "同一次展开只采集一次，关闭后重新允许，关闭本身不采集");
            UsageNode deep = button("用量：5 小时限额的 52%"); for (int i = 0; i < 45; i++) deep = group(new[] { deep });
            Check(UsageInterface.ReadSynthetic(page(code, new[] { deep }), 5).FiveHour == null, "超过 40 层不采用");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { button("用量：5 小时限额的 52%") }), -1).Status == "界面读取未完成", "预算耗尽不采用");
            // 合并：只给一项时另一项沿用同一身份的旧值与原时间；身份变化不沿用；暂不可读保留旧值。
            DateTime t1 = new DateTime(2026, 10, 29, 9, 0, 0), t2 = t1.AddMinutes(30);
            var first = UsageInterface.Merge(null, new UsageRecord { Status = UsageInterface.Observed, FiveHour = new UsageMetric { Percent = 40, Reset = "3:20 重置" } }, "2|10|5|1|x", t1);
            var second = UsageInterface.Merge(first, new UsageRecord { Status = UsageInterface.Observed, Weekly = new UsageMetric { Percent = 9 } }, "2|10|5|1|x", t2);
            Check(second.FiveHour.Percent == 40 && second.FiveHour.Observed == t1 && second.Weekly.Observed == t2, "合并应保留另一项与原时间");
            Check(UsageInterface.Merge(first, new UsageRecord { Status = UsageInterface.Observed, Weekly = new UsageMetric { Percent = 9 } }, "2|11|5|1|x", t2).FiveHour == null, "身份变化不得沿用旧值");
            Check(UsageInterface.Merge(second, new UsageRecord { Status = "界面读取未完成" }, "2|10|5|1|x", t2) == second && UsageInterface.Merge(second, new UsageRecord { Status = "多个用量界面，待核对" }, "2|10|5|1|x", t2).Status != UsageInterface.Observed, "暂不可读保留旧值，不确定结果不沿用");
            Check(UsageInterface.TimeLabel(second.FiveHour, t2) == "09:00:00" && UsageInterface.TimeLabel(second.FiveHour, t1.AddDays(1)) == "10-29 09:00" && UsageInterface.TimeLabel(null, t2) == "尚未观察到", "观察时间格式错误");
            string usageFile = Path.Combine(root, "settings", "usage-observations.json");
            Check(UsageInterface.Load(usageFile, t2).Count == 0, "缺少观察记录应为空");
            UsageInterface.Save(usageFile, new[] { second, new UsageRecord { Status = "界面读取未完成" } });
            var restored = UsageInterface.Load(usageFile, t2.AddMinutes(1));
            Check(restored.Count == 1 && restored[2].Identity == "2|10|5|1|x" && restored[2].FiveHour.Percent == 40 && restored[2].FiveHour.Observed == t1 && restored[2].Weekly.Reset == "", "观察记录往返错误");
            Check(!File.ReadAllText(usageFile).Contains("epitaxy") && UsageInterface.Load(usageFile, t1.AddMinutes(-1)).Count == 0, "不得保存页面；未来时间必须拒绝");
            File.WriteAllText(usageFile, "{\"version\":1,\"records\":[{\"identity\":\"3|1|1|0|x\",\"fh\":{\"percent\":5,\"reset\":\"\",\"observed\":1000}},{\"identity\":\"3|2|1|0|x\",\"fh\":{\"percent\":6,\"reset\":\"\",\"observed\":1000}},{\"identity\":\"11|1|1|0|x\",\"fh\":{\"percent\":5,\"reset\":\"\",\"observed\":1000}},{\"identity\":\"4|1|1|0|x\",\"fh\":{\"percent\":105,\"reset\":\"\",\"observed\":1000}}]}");
            Check(UsageInterface.Load(usageFile, t2).Count == 0, "重复账号、越界编号与百分比必须丢弃");
            foreach (string broken in new[] { "{", "null", "{\"version\":2,\"records\":[]}" }) { File.WriteAllText(usageFile, broken); Check(UsageInterface.Load(usageFile, t2).Count == 0, "损坏观察记录应为空：" + broken); }
            // 开机启动：只接受正式安装路径；值比较不区分大小写、需带引号的完整路径。（不读写真实注册表）
            string installed = @"C:\Users\x\AppData\Local\Programs\Claude 多开启动器\ClaudeLauncher.exe";
            Check(AutoStart.IsInstalledCopy(installed.ToUpperInvariant(), installed) && !AutoStart.IsInstalledCopy(@"D:\ClaudeLauncher\windows\artifacts\bin\ClaudeLauncher.exe", installed), "正式安装路径判断错误");
            Check(AutoStart.PointsTo("\"" + installed + "\"", installed) && !AutoStart.PointsTo(installed, installed) && !AutoStart.PointsTo("\"C:\\other.exe\"", installed) && !AutoStart.PointsTo(null, installed), "开机启动值比较错误");
            Check(Updates.SignerProblem(null, null) == null && Updates.SignerProblem("AA", "aa") == null, "签名者规则错误");
            Check(Updates.SignerProblem("AA", null) != null && Updates.SignerProblem("AA", "BB") != null, "签名者不一致必须拒绝");
            string unsigned = Path.Combine(root, "unsigned.exe");
            File.WriteAllBytes(unsigned, new byte[4096]);
            Check(Updates.Signer(unsigned) == null, "未签名文件应返回空签名者");
        }
        static void Junction(string link, string target) {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var process = Process.Start(info)) { process.StandardOutput.ReadToEnd(); process.WaitForExit(); Check(process.ExitCode == 0, "无法创建目录链接夹具"); }
        }
        static void Cleanup(string root) {
            string full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("claude-selftest-")) throw new IOException("自检清理路径无效");
            if (!Directory.Exists(full)) return;
            // 先移除夹具内残留的链接本身，再递归删除，避免跟随链接。
            string extended = Storage.Extended(full);
            foreach (string entry in Directory.EnumerateDirectories(extended, "*", SearchOption.AllDirectories).ToArray())
                if (Directory.Exists(entry) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry);
            Directory.Delete(extended, true);
        }
    }
}
