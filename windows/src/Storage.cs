using System;
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
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info))
                    throw new IOException("无法检查存储项：" + path, Marshal.GetHRForLastWin32Error());
                return (int)info.Links;
            }
        }
        static FileAttributes? Attributes(string path) {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        public static void Validate(int number, string roaming, string local) {
            var user = WindowsIdentity.GetCurrent().User;
            foreach (string folder in ProfileDeletion.Candidates(number, roaming, local)) {
                for (string entry = folder; entry != null; entry = Path.GetDirectoryName(entry)) {
                    FileAttributes? attributes = Attributes(entry);
                    if (attributes == null) continue;
                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("账号目录路径含链接或重定向，已停止：" + entry);
                    if ((attributes.Value & FileAttributes.Directory) == 0) throw new IOException("账号目录路径不是文件夹，已停止：" + entry);
                }
                if (Attributes(folder) == null) continue;
                foreach (string name in Items) {
                    string path = Path.Combine(folder, name);
                    FileAttributes? attributes = Attributes(path);
                    if (attributes == null) continue;
                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("存储项是链接，已停止：" + path);
                    bool directory = (attributes.Value & FileAttributes.Directory) != 0;
                    FileSystemSecurity security = directory ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Owner) : File.GetAccessControl(path, AccessControlSections.Owner);
                    if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("存储项不属于当前用户，已停止：" + path);
                    if (!directory && LinkCount(path) > 1) throw new IOException("存储项是硬链接，已停止：" + path);
                }
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
                Junction(folders[2], target);
                Rejects(() => Storage.Validate(3, roaming, local), "链接账号目录必须拒绝");
                Directory.Delete(folders[2]);
            } finally { Cleanup(root); }
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
