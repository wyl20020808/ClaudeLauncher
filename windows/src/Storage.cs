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

        public static int LinkCount(string path) {
            // FILE_READ_ATTRIBUTES，共享全部；BACKUP_SEMANTICS | OPEN_REPARSE_POINT 不跟随链接。
            using (SafeFileHandle handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero)) {
                FileInfoByHandle info;
                if (handle.IsInvalid) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 2 || error == 3) return 0; // 检查期间被 Claude 删除的缓存文件。
                    throw new IOException("无法检查存储项：" + path, Marshal.GetHRForLastWin32Error());
                }
                if (!GetFileInformationByHandle(handle, out info)) throw new IOException("无法检查存储项：" + path, Marshal.GetHRForLastWin32Error());
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
        public static void Validate(int number, string roaming, string local, bool recursive) {
            var user = WindowsIdentity.GetCurrent().User;
            foreach (string folder in ProfileDeletion.Candidates(number, roaming, local)) {
                for (string entry = folder; entry != null; entry = Path.GetDirectoryName(entry)) {
                    FileAttributes? attributes = Attributes(entry);
                    if (attributes == null) continue;
                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("账号目录路径含链接或重定向，已停止：" + entry);
                    if ((attributes.Value & FileAttributes.Directory) == 0) throw new IOException("账号目录路径不是文件夹，已停止：" + entry);
                }
                if (Attributes(folder) == null) continue;
                foreach (string name in Items) Check(Path.Combine(folder, name), user, recursive);
            }
        }
        // 逐项检查存储项及其内部：不跟随链接，检查期间消失的条目跳过，其余异常一律停止。
        static void Check(string root, SecurityIdentifier user, bool recursive) {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0) {
                string path = pending.Pop();
                FileAttributes? attributes = Attributes(path);
                if (attributes == null) continue;
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("存储项含链接或重定向，已停止：" + path);
                bool directory = (attributes.Value & FileAttributes.Directory) != 0;
                SecurityIdentifier owner;
                try {
                    FileSystemSecurity security = directory ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Owner) : File.GetAccessControl(path, AccessControlSections.Owner);
                    owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
                } catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
                catch (UnauthorizedAccessException) { throw new IOException("无法读取存储项所有者，已停止：" + path); }
                if (!user.Equals(owner)) throw new IOException("存储项不属于当前用户，已停止：" + path);
                if (!directory && LinkCount(path) > 1) throw new IOException("存储项是硬链接，已停止：" + path);
                if (!directory || !recursive) continue;
                try { foreach (string entry in Directory.EnumerateFileSystemEntries(path)) pending.Push(entry); }
                catch (DirectoryNotFoundException) { }
                catch (UnauthorizedAccessException) { throw new IOException("无法读取存储目录，已停止：" + path); }
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
                var mac = new Release { platform = "macos", version = "1.8.0", file = "Claude多开启动器-1.8.0.dmg", size = 4096, sha256 = new string('c', 64), minimumOS = "12.0" };
                Check(Updates.Verify(sign(mac), key, "macos").file == "Claude多开启动器-1.8.0.dmg", "macOS 清单验签失败");
                Check(System.Text.Encoding.UTF8.GetString(Updates.Payload(mac)).StartsWith("macos\n1.8.0\nClaude多开启动器-1.8.0.dmg\n4096\n"), "macOS 签名原文错误");
            }
            Check(Updates.Compare("1.10.0", "1.9.9") > 0 && Updates.Compare("1.7.0", "1.7") == 0 && Updates.Compare("1.6.9", "1.7.0") < 0, "版本比较错误");
            Check(!Updates.IsNewer(new Release { version = Updates.DisplayVersion }) && !Updates.IsNewer(new Release { version = "1.6.0" }) && Updates.IsNewer(new Release { version = "1.7.1" }), "只升级、不降级");
            Check(Updates.OsSupported("10.0.19041", new Version(10, 0, 26200)) && !Updates.OsSupported("10.0.19041", new Version(10, 0, 18363)), "系统版本比较错误");
            Check(Updates.RedirectAllowed(new Uri("https://objects.githubusercontent.com/x"), true) && Updates.RedirectAllowed(new Uri("https://release-assets.githubusercontent.com/x"), true) && Updates.RedirectAllowed(new Uri("https://github.com/x"), true), "GitHub 下载域名应允许");
            Check(!Updates.RedirectAllowed(new Uri("https://evil.example/x"), true) && !Updates.RedirectAllowed(new Uri("http://github.com/x"), true) &&
                !Updates.RedirectAllowed(new Uri("https://github.com:8443/x"), true) && !Updates.RedirectAllowed(new Uri("https://github.com.evil.example/x"), true), "未允许的跳转必须拒绝");
            Check(!Updates.RedirectAllowed(new Uri("https://github.com/x"), false), "自建服务不允许任何跳转");
            Check(Updates.Sources().Last().Base == Updates.GitHubBase && Updates.Sources().All(s => s.Base.StartsWith("https://")), "更新来源顺序或协议错误");
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
            foreach (string entry in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories).ToArray())
                if (Directory.Exists(entry) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry);
            Directory.Delete(full, true);
        }
    }
}
