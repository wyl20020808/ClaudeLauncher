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
    public sealed class StorageNotFolderException : IOException {
        public StorageNotFolderException(string message) : base(message) { }
    }
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileNameW(string name, uint flags, ref uint length, System.Text.StringBuilder link);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileNameW(IntPtr find, ref uint length, System.Text.StringBuilder link);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr find);
        // 硬链接的全部名字（卷内路径，不含盘符，如 \Users\me\x）；只读目录项，不打开内容。
        public static List<string> LinkNames(string path) {
            var names = new List<string>(); var buffer = new System.Text.StringBuilder(32768); uint length = 32768;
            IntPtr find = FindFirstFileNameW(Extended(path), 0, ref length, buffer);
            if (find == new IntPtr(-1)) throw new IOException("无法检查存储项：" + Display(path), Marshal.GetHRForLastWin32Error());
            try {
                do { names.Add(buffer.ToString()); buffer.Length = 0; length = 32768; } while (FindNextFileNameW(find, ref length, buffer));
                if (Marshal.GetLastWin32Error() != 38) throw new IOException("无法检查存储项：" + Display(path)); // 38 = ERROR_HANDLE_EOF
            } finally { FindClose(find); }
            return names;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetLongPathNameW(string shortPath, System.Text.StringBuilder longPath, uint length);
        // 只在确有外部别名时检查此目录；自检使用自己的临时目录。
        public static string TaskTemp = Path.Combine(Path.GetTempPath(), "claude");
        static string LongPath(string path) {
            string full = Path.GetFullPath(Display(path));
            if (Path.GetPathRoot(full).Length != 3 || full[1] != ':') throw new IOException("无法确认存储项所在的本地卷。");
            var buffer = new System.Text.StringBuilder(32768);
            uint length = GetLongPathNameW(Extended(full), buffer, (uint)buffer.Capacity);
            if (length == 0 || length >= buffer.Capacity) throw new IOException("无法规范化存储路径：" + Display(path));
            return Display(buffer.ToString()).TrimEnd('\\');
        }
        static string VolumeRelative(string full) { return full.Substring(Path.GetPathRoot(full).Length - 1); }
        const string SessionId = @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
        static readonly System.Text.RegularExpressions.Regex SessionName = new System.Text.RegularExpressions.Regex(
            @"\AClaudeCodeConfig\\projects\\[^\\]+\\" + SessionId + @"\.jsonl\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 账号内子代理记录：ClaudeCodeConfig\projects\<项目>\<会话 ID>\subagents\agent-<代理 ID>.jsonl
        static readonly System.Text.RegularExpressions.Regex SubagentName = new System.Text.RegularExpressions.Regex(
            @"\AClaudeCodeConfig\\projects\\[^\\]+\\(" + SessionId + @")\\subagents\\agent-([0-9A-Za-z]+)\.jsonl\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static bool DataName(string name) {
            return name.StartsWith(@"ClaudeCodeConfig\file-history\", StringComparison.OrdinalIgnoreCase) || SessionName.IsMatch(name) || SubagentName.IsMatch(name);
        }
        static bool Inside(string path, string root) { return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase); }
        [DllImport("advapi32.dll")] static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        static SafeFileHandle OpenMetadata(string path) {
            return CreateFile(Extended(path), 0x20080, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); // READ_CONTROL | FILE_READ_ATTRIBUTES，全共享、不跟随链接。
        }
        static FileInfoByHandle Metadata(SafeFileHandle handle, string path, bool directory, bool systemAncestor = false) {
            FileInfoByHandle info;
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) throw new IOException("无法读取存储项元数据：" + Display(path));
            if ((info.Attributes & (0x400 | 0x40)) != 0 || ((info.Attributes & 0x10) != 0) != directory) throw new IOException("存储路径含链接、重定向或类型异常：" + Display(path));
            IntPtr owner, group, dacl, sacl, descriptor;
            uint error = GetSecurityInfo(handle, 1, 1, out owner, out group, out dacl, out sacl, out descriptor);
            try {
                if (error != 0 || owner == IntPtr.Zero) throw new IOException("无法读取存储项所有者：" + Display(path));
                var sid = new SecurityIdentifier(owner);
                // 仅允许临时／账号根目录上方的系统祖先归 SYSTEM 或 TrustedInstaller；数据目录本身仍须本人所有。
                bool system = systemAncestor && (sid.Value == "S-1-5-18" || sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
                if (!OwnedByMe(sid) && !system) throw new IOException("存储项不属于当前用户：" + Display(path));
            } finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
            return info;
        }
        static void CheckParents(string path, string root) {
            var parents = new Stack<string>();
            for (string entry = Path.GetDirectoryName(path); entry != null; entry = Path.GetDirectoryName(entry)) parents.Push(entry);
            while (parents.Count > 0) {
                string entry = parents.Pop();
                using (var handle = OpenMetadata(entry)) Metadata(handle, entry, true, !Inside(entry.TrimEnd('\\'), root));
            }
        }
        static bool SameFile(FileInfoByHandle a, FileInfoByHandle b) { return a.Volume == b.Volume && a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow; }
        internal static Action<string> AfterLinkEnumeration; // 自检制造链接增减、替换或读取失败；正式运行为空。
        // 硬链接放行规则（SPEC 存储校验硬链接例外）：
        // 1. 账号内的每个名字都必须是 file-history 或明确形状的会话／子代理记录，敏感名字不能借用豁免。
        // 2. 账号外的名字只能是 %TEMP%\claude\<一层目录>\<会话 ID>\tasks\<代理 ID>.output，且同一文件在本账号有对应的
        //    subagents\agent-<代理 ID>.jsonl（会话 ID、代理 ID 都相同）：Claude Code 把子代理记录硬链接成后台任务输出（2026-11-07 实测 12/12）。
        // 其他与账号目录外（含其他账号）共用的硬链接仍拒绝。
        public static bool LinksWithin(string path, string folder) {
            for (int attempt = 0; ; attempt++) {
                try {
                    using (var handle = OpenMetadata(path)) {
                        var before = Metadata(handle, path, false);
                        var names = LinkNames(path);
                        if (AfterLinkEnumeration != null) AfterLinkEnumeration(path);
                        if (names.Count != before.Links || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                            throw new IOException("硬链接枚举数量与文件元数据不一致。");
                        bool allowed = NamesAllowed(names, LongPath(folder), before);
                        var after = Metadata(handle, path, false);
                        using (var current = OpenMetadata(path)) {
                            var now = Metadata(current, path, false);
                            if (!SameFile(before, now) || before.Links != after.Links || before.Links != now.Links)
                                throw new IOException("硬链接检查期间文件或链接数发生变化。");
                        }
                        return allowed;
                    }
                } catch (IOException error) {
                    if (attempt == 1) throw new IOException("无法确认存储项硬链接隔离（已检查两次），已停止：" + Display(path), error);
                }
            }
        }
        static bool NamesAllowed(List<string> names, string full, FileInfoByHandle expected) {
            string prefix = VolumeRelative(full) + "\\";
            var inside = names.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inside.Count == 0 || inside.Any(name => !DataName(name.Substring(prefix.Length)))) return false;
            var outside = names.Where(name => !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            string temp = outside.Count == 0 ? null : LongPath(TaskTemp);
            if (temp != null && (!string.Equals(Path.GetPathRoot(temp), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase) || Inside(temp, full) || Inside(full, temp))) return false;
            var allowed = new List<System.Text.RegularExpressions.Regex>();
            foreach (string name in inside) {
                var match = SubagentName.Match(name.Substring(prefix.Length));
                if (temp != null && match.Success) allowed.Add(new System.Text.RegularExpressions.Regex(@"\A" + System.Text.RegularExpressions.Regex.Escape(VolumeRelative(temp) + "\\") + @"[^\\]+\\"
                    + match.Groups[1].Value + @"\\tasks\\" + match.Groups[2].Value + @"\.output\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            }
            if (outside.Any(name => !allowed.Any(rule => rule.IsMatch(name)))) return false;
            foreach (string name in names) {
                string alias = Path.GetPathRoot(full).TrimEnd('\\') + name;
                CheckParents(alias, name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : temp);
                using (var handle = OpenMetadata(alias)) {
                    var actual = Metadata(handle, alias, false);
                    if (!SameFile(expected, actual) || expected.Links != actual.Links) throw new IOException("硬链接检查期间别名或链接数发生变化。");
                }
            }
            return true;
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
        // 接受当前用户、令牌默认所有者，以及本机 Administrators 组：提权或关闭 UAC 时建的目录归该组，之后不提权运行也要认（该组本就能完全控制本人目录）。
        static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        public static bool OwnedByMe(SecurityIdentifier owner) {
            var me = WindowsIdentity.GetCurrent();
            return owner != null && (owner.Equals(me.User) || owner.Equals(me.Owner) || owner.Equals(Administrators));
        }
        public static void Validate(int number, string roaming, string local, bool recursive) {
            string[] folders = ProfileDeletion.Candidates(number, roaming, local);
            // 不是文件夹时确实无法启动：先于其他原因报告，“仍然继续”不能越过（SPEC 存储校验）。
            foreach (string folder in folders)
                for (string entry = folder; entry != null; entry = Path.GetDirectoryName(entry)) {
                    FileAttributes? attributes = Attributes(entry);
                    if (attributes != null && (attributes.Value & FileAttributes.Directory) == 0) throw new StorageNotFolderException("账号目录路径不是文件夹，已停止：" + entry);
                }
            foreach (string folder in folders) {
                for (string entry = folder; entry != null; entry = Path.GetDirectoryName(entry)) {
                    FileAttributes? attributes = Attributes(entry);
                    if (attributes == null) continue;
                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw new IOException("账号目录路径含链接或重定向，已停止：" + entry);
                }
                if (Attributes(folder) == null) continue;
                foreach (string name in Items) Check(Path.Combine(folder, name), recursive, folder);
            }
        }
        // 逐项检查存储项及其内部：不跟随链接，检查期间消失的条目跳过，其余异常一律停止。
        static void Check(string root, bool recursive, string folder) {
            var pending = new Stack<string>(); pending.Push(Extended(root));
            while (pending.Count > 0) {
                string path = pending.Pop(), shown = Display(path);
                FileAttributes? attributes = Attributes(path);
                if (attributes == null) continue;
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0) {
                    bool shared;
                    try { shared = ConfigSync.SharedLinkAllowed(root, shown); }
                    catch (IOException error) { throw new IOException(error.Message.TrimEnd('。') + "：" + shown, error); }
                    if (shared) continue;
                    throw new IOException("存储项含链接或重定向，已停止：" + shown);
                }
                bool directory = (attributes.Value & FileAttributes.Directory) != 0;
                SecurityIdentifier owner;
                try {
                    FileSystemSecurity security = directory ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Owner) : File.GetAccessControl(path, AccessControlSections.Owner);
                    owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
                } catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
                catch (UnauthorizedAccessException) { throw new IOException("无法读取存储项所有者，已停止：" + shown); }
                if (!OwnedByMe(owner)) throw new IOException("存储项不属于当前用户，已停止：" + shown);
                if (!directory && LinkCount(path) > 1 && !LinksWithin(path, folder)) throw new IOException("存储项硬链接包含不允许的位置，已停止：" + shown);
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
                Check(Storage.OwnedByMe(me.User) && Storage.OwnedByMe(me.Owner) && Storage.OwnedByMe(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) && !Storage.OwnedByMe(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) && !Storage.OwnedByMe(null), "所有者判断错误");
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
                // 白名单内的 skills 目录联接（mklink /J 安装的 skill）放行；指向受保护目录上级的仍拒绝，报错带路径
                string skill = Path.Combine(Core.CodeConfig(folder), @"skills\shared");
                Directory.CreateDirectory(Path.GetDirectoryName(skill));
                Junction(skill, target);
                Check(ConfigSync.LinkTarget(skill) == target, "目录联接目标解析错误");
                Storage.Validate(2, roaming, local);
                Directory.Delete(skill);
                Junction(skill, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                try { Storage.Validate(2, roaming, local); Check(false, "指向用户目录（~/.claude 上级）的联接必须拒绝"); }
                catch (IOException error) { Check(error.Message.Contains(@"skills\shared"), "联接拒绝提示缺少路径"); }
                Directory.Delete(skill);
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
                // 只有会话数据位置可共享；账号内的缓存、敏感配置也不能借用豁免。
                string history = Path.Combine(Core.CodeConfig(folder), "file-history");
                Directory.CreateDirectory(Path.Combine(history, "a")); Directory.CreateDirectory(Path.Combine(history, "b"));
                File.WriteAllText(Path.Combine(history, @"a\x@v3"), "x");
                Check(CreateHardLink(Path.Combine(history, @"b\x@v3"), Path.Combine(history, @"a\x@v3"), IntPtr.Zero), "无法创建账号内硬链接夹具");
                Check(Storage.LinkNames(Path.Combine(history, @"a\x@v3")).Count == 2, "硬链接名字数量错误");
                Storage.Validate(2, roaming, local);
                Check(CreateHardLink(Storage.Extended(longFile), Path.Combine(history, @"a\x@v3"), IntPtr.Zero), "无法创建长路径账号内硬链接夹具");
                Rejects(() => Storage.Validate(2, roaming, local), "账号内缓存名字不能借用 file-history 豁免");
                File.Delete(longFile);
                string longHistory = Storage.Extended(Path.Combine(history, new string('d', 120), new string('e', 120), "x@v3"));
                Directory.CreateDirectory(Path.GetDirectoryName(longHistory));
                Check(CreateHardLink(longHistory, Path.Combine(history, @"a\x@v3"), IntPtr.Zero), "无法创建长路径备份硬链接夹具");
                Storage.Validate(2, roaming, local);
                File.Delete(longHistory);
                Directory.CreateDirectory(folders[2]);
                Check(CreateHardLink(Path.Combine(folders[2], "shared"), Path.Combine(history, @"a\x@v3"), IntPtr.Zero), "无法创建跨账号硬链接夹具");
                Rejects(() => Storage.Validate(2, roaming, local), "与其他账号共用的硬链接必须拒绝");
                File.Delete(Path.Combine(folders[2], "shared"));
                Directory.CreateDirectory(Path.Combine(folder, "Network"));
                Check(CreateHardLink(Path.Combine(folder, @"Network\x"), outside, IntPtr.Zero) && CreateHardLink(Path.Combine(folder, "Preferences"), outside, IntPtr.Zero), "无法创建账号内外混合硬链接夹具");
                Rejects(() => Storage.Validate(2, roaming, local), "账号内外混合的硬链接必须拒绝");
                File.Delete(outside);
                Rejects(() => Storage.Validate(2, roaming, local), "仅剩账号内部敏感名字的硬链接仍须拒绝");
                File.Delete(Path.Combine(folder, @"Network\x")); File.Delete(Path.Combine(folder, "Preferences"));
                Directory.Delete(Storage.Extended(history), true); Directory.Delete(folders[2]);
                Storage.Validate(2, roaming, local);
                // 子代理记录与 %TEMP%\claude\<目录>\<会话>\tasks\<代理>.output 共用硬链接放行（Claude Code 后台任务输出）；其他形状仍拒绝
                string savedTemp = Storage.TaskTemp;
                try {
                    Storage.TaskTemp = Path.Combine(root, @"Temp\claude");
                    const string sid = "4f2d3219-db6e-4c05-9e39-2556b17bf35a", otherSid = "11111111-2222-3333-4444-555555555555";
                    string subagents = Path.Combine(Core.CodeConfig(folder), @"projects\D--proj\" + sid + @"\subagents");
                    Directory.CreateDirectory(subagents);
                    string transcript = Path.Combine(subagents, "agent-afa6c355ac2b14d0b.jsonl");
                    File.WriteAllText(transcript, "{}");
                    Action<string, bool, string> taskLink = (relative, allowed, message) => {
                        string link = Path.Combine(Storage.TaskTemp, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(link));
                        Check(CreateHardLink(link, transcript, IntPtr.Zero), "无法创建任务输出硬链接夹具：" + relative);
                        if (allowed) Storage.Validate(2, roaming, local); else Rejects(() => Storage.Validate(2, roaming, local), message);
                        File.Delete(link);
                    };
                    taskLink(@"a20177659752a04a\" + sid + @"\tasks\afa6c355ac2b14d0b.output", true, null);
                    taskLink(@"a20177659752a04a\" + otherSid + @"\tasks\afa6c355ac2b14d0b.output", false, "会话 ID 不同的任务输出硬链接必须拒绝");
                    taskLink(@"a20177659752a04a\" + sid + @"\tasks\a000000000000000.output", false, "代理 ID 不同的任务输出硬链接必须拒绝");
                    taskLink(@"a20177659752a04a\" + sid + @"\other\afa6c355ac2b14d0b.output", false, "不在 tasks 下的硬链接必须拒绝");
                    taskLink(@"x\y\" + sid + @"\tasks\afa6c355ac2b14d0b.output", false, "多一层目录的任务输出硬链接必须拒绝");
                    taskLink(sid + @"\tasks\afa6c355ac2b14d0b.output", false, "少一层目录的任务输出硬链接必须拒绝");
                    // 账号外名字不在 Temp\claude：拒绝
                    Check(CreateHardLink(Path.Combine(root, "afa6c355ac2b14d0b.output"), transcript, IntPtr.Zero), "无法创建账号外硬链接夹具");
                    Rejects(() => Storage.Validate(2, roaming, local), "临时目录外的子代理硬链接必须拒绝");
                    File.Delete(Path.Combine(root, "afa6c355ac2b14d0b.output"));
                    // 非子代理记录（如 Preferences）与任务输出共用：拒绝
                    string plain = Path.Combine(Core.CodeConfig(folder), @"projects\D--proj\" + sid + @"\afa6c355ac2b14d0b.jsonl");
                    File.WriteAllText(plain, "{}");
                    string plainLink = Path.Combine(Storage.TaskTemp, @"h\" + sid + @"\tasks\afa6c355ac2b14d0b.output");
                    Directory.CreateDirectory(Path.GetDirectoryName(plainLink));
                    Check(CreateHardLink(plainLink, plain, IntPtr.Zero), "无法创建非子代理硬链接夹具");
                    Rejects(() => Storage.Validate(2, roaming, local), "非子代理记录与任务输出共用必须拒绝");
                    File.Delete(plainLink); File.Delete(plain);
                    // 任务输出合法，但同时又与其他账号共用：拒绝
                    string okLink = Path.Combine(Storage.TaskTemp, @"h\" + sid + @"\tasks\afa6c355ac2b14d0b.output");
                    Directory.CreateDirectory(folders[2]);
                    Check(CreateHardLink(okLink, transcript, IntPtr.Zero) && CreateHardLink(Path.Combine(folders[2], "shared"), transcript, IntPtr.Zero), "无法创建任务输出与跨账号混合夹具");
                    Rejects(() => Storage.Validate(2, roaming, local), "任务输出之外又与其他账号共用必须拒绝");
                    File.Delete(Path.Combine(folders[2], "shared")); File.Delete(okLink); Directory.Delete(folders[2]);
                    // 同一文件混入敏感／未知名字，无论有无任务输出都拒绝。
                    Check(CreateHardLink(okLink, transcript, IntPtr.Zero), "无法创建合法任务输出夹具");
                    foreach (string relative in new[] { "Preferences", @"Network\Cookies", @"ClaudeCodeConfig\.claude.json", @"ClaudeCodeConfig\settings.json", @"ClaudeCodeConfig\projects\D--proj\settings.json", @"ClaudeCodeConfig\projects-other\record.jsonl", "unlisted.txt" }) {
                        string mixed = Path.Combine(folder, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(mixed));
                        Check(CreateHardLink(mixed, transcript, IntPtr.Zero), "无法创建混合名字夹具");
                        Check(!Storage.LinksWithin(transcript, folder) && !Storage.LinksWithin(mixed, folder), "敏感或未知名字不能借用子代理及任务输出豁免");
                        Rejects(() => Storage.Validate(2, roaming, local), "混合名字必须阻止完整存储校验");
                        File.Delete(mixed);
                    }
                    File.Delete(okLink);
                    string sessionAlias = Path.Combine(Core.CodeConfig(folder), @"projects\D--proj\" + sid + ".jsonl");
                    Check(CreateHardLink(sessionAlias, transcript, IntPtr.Zero), "无法创建账号内会话关联夹具");
                    Check(Storage.LinksWithin(transcript, folder), "合法的会话与子代理内部关联应通过");
                    Storage.Validate(2, roaming, local);
                    Rejects(() => { using (LocalFiles.OpenRead(transcript, 1024)) { } }, "启动允许的会话硬链接仍不能作为同步读取来源");
                    Check(LocalFiles.FileState(sessionAlias) == null, "启动允许的会话硬链接仍不能作为同步替换目标");
                    string sensitive = Path.Combine(folder, "Preferences");
                    Check(CreateHardLink(sensitive, transcript, IntPtr.Zero), "无法创建内部敏感名字夹具");
                    Rejects(() => Storage.Validate(2, roaming, local), "没有外部名字时也不能放过敏感别名");
                    File.Delete(sensitive); File.Delete(sessionAlias);
                    Check(CreateHardLink(okLink, transcript, IntPtr.Zero), "无法创建规范化路径夹具");
                    string taskRoot = Storage.TaskTemp;
                    Storage.TaskTemp = Path.Combine(taskRoot, ".") + "\\";
                    Check(Storage.LinksWithin(transcript, folder), "临时目录尾斜杠和点路径应规范化");
                    Storage.TaskTemp = taskRoot;
                    string redirected = Path.Combine(root, "redirected-temp");
                    Junction(redirected, Path.GetDirectoryName(taskRoot));
                    try {
                        Storage.TaskTemp = Path.Combine(redirected, "claude");
                        Rejects(() => { if (!Storage.LinksWithin(transcript, folder)) throw new IOException(); }, "临时根目录上级重定向必须拒绝");
                    } finally { Storage.TaskTemp = taskRoot; Directory.Delete(redirected); }
                    string tasks = Path.GetDirectoryName(okLink), parked = Path.Combine(root, "parked-tasks");
                    try {
                        Storage.AfterLinkEnumeration = path => {
                            if (Directory.Exists(parked)) return;
                            Directory.Move(tasks, parked); Junction(tasks, parked);
                        };
                        Rejects(() => { if (!Storage.LinksWithin(transcript, folder)) throw new IOException(); }, "枚举后临时目录内部变成重解析点必须拒绝");
                    } finally {
                        Storage.AfterLinkEnumeration = null;
                        if (Directory.Exists(parked)) { Directory.Delete(tasks); Directory.Move(parked, tasks); }
                    }
                    // 查真实目录项身份：扫描中增减链接或替换原路径，不能沿用旧枚举结果。
                    string moving = Path.Combine(subagents, "agent-moving.jsonl");
                    int scans = 0;
                    try {
                        Storage.AfterLinkEnumeration = path => {
                            scans++;
                            if (File.Exists(moving)) File.Delete(moving);
                            else if (!CreateHardLink(moving, transcript, IntPtr.Zero)) throw new IOException("无法创建变化夹具");
                        };
                        Rejects(() => Storage.LinksWithin(transcript, folder), "扫描期间持续变化必须报告无法确认");
                        Check(scans == 2, "不稳定扫描只能尝试两次");
                        if (File.Exists(moving)) File.Delete(moving);
                        scans = 0;
                        Storage.AfterLinkEnumeration = path => { if (++scans == 1 && !CreateHardLink(moving, transcript, IntPtr.Zero)) throw new IOException("无法创建变化夹具"); };
                        Check(Storage.LinksWithin(transcript, folder) && scans == 2, "首次变化后应重新完整检查，稳定且合法才通过");
                        File.Delete(moving);
                        Storage.AfterLinkEnumeration = path => { File.Delete(path); File.WriteAllText(path, "replacement"); };
                        Rejects(() => Storage.LinksWithin(transcript, folder), "扫描中路径被替换必须拒绝");
                        File.Delete(transcript);
                        Check(CreateHardLink(transcript, okLink, IntPtr.Zero), "无法恢复身份变化夹具");
                        scans = 0;
                        Storage.AfterLinkEnumeration = path => { scans++; throw new IOException("模拟扫描元数据失败"); };
                        Rejects(() => Storage.LinksWithin(transcript, folder), "扫描失败不能当作通过");
                        Check(scans == 2, "读取失败重试必须有界");
                    } finally { Storage.AfterLinkEnumeration = null; if (File.Exists(moving)) File.Delete(moving); }
                    File.Delete(okLink);
                    Rejects(() => Storage.LinkNames(Path.Combine(root, "missing-file")), "原生枚举失败必须报告错误");
                    Directory.Delete(Path.Combine(Core.CodeConfig(folder), "projects"), true);
                    Storage.Validate(2, roaming, local);
                } finally { Storage.TaskTemp = savedTemp; }
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
            Check(UsageInterface.ButtonMetric("Usage: Weekly · All models: 12% Resets Wed 3pm").Item1 == "sd" && UsageInterface.ButtonMetric("用量：每周\u00a0·\u00a0所有模型：12%").Item2 == 12, "英文周值与不换行空格应被识别");
            Check(UsageInterface.ButtonMetric("Usage: Weekly · Opus: 12%") == null, "英文模型专属周限额不得混入");
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
            Check(read.Detail.Contains("reason=panel_not_found") && !read.Detail.Contains("11%") && !read.Detail.Contains("local_"), "缺面板诊断只含原因和计数");
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
            Check(read.Detail.Contains("reason=complete"), "完整面板应报告成功原因");
            var englishPanel = UsageInterface.ReadSynthetic(page(code, new[] { button("Usage: Weekly · All models: 11%"), popup("https://claude.ai/settings/usage", new[] { bar("5-hour limit", 71), bar("Weekly\u00a0· All models", 11) }) }), 5);
            Check(englishPanel.FiveHour.Percent == 71 && englishPanel.Weekly.Percent == 11, "英文面板大小写与不换行空格兼容");
            var missingPanelMetric = UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://claude.ai/settings/usage", new[] { bar("5 小时限额", 71) }) }), 5);
            Check(missingPanelMetric.Detail.Contains("reason=panel_missing_button_metric"), "面板缺少按钮指标应单独诊断");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { button("用量：每周 · 所有模型：12%周二19:00 重置"), popup("https://claude.ai/settings/usage", limits) }), 5).Status == "界面数据待核对", "面板与按钮不一致必须拒绝");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://evil.test/settings/usage", limits) }), 5).FiveHour == null, "没有官方用量链接的弹窗不得读取");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://claude.ai/settings/usage", limits.Concat(new[] { bar("5 小时限额", 70) }).ToArray()) }), 5).Status == "界面数据待核对", "重复指标必须拒绝");
            Check(UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://claude.ai/settings/usage", limits), popup("https://claude.ai/settings/usage", limits) }), 5).Status == "界面数据待核对", "多个面板必须拒绝");
            read = UsageInterface.ReadSynthetic(page(code, new[] { weekButton, popup("https://claude.ai/settings/usage", new[] { bar("每周 · Opus", 40), bar("每周 · 所有模型", 11), bar("上下文", 52) }) }), 5);
            Check(read.Status == UsageInterface.Observed && read.Weekly.Percent == 11 && read.FiveHour == null, "模型专属周限额与上下文不得采用");
            Check(read.Detail.Contains("reason=panel_incomplete") && read.Detail.Contains("unknownBars=2"), "缺指标与无面板必须区分，不输出未知控件文字");
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
            var weekly9 = new UsageRecord { Status = UsageInterface.Observed, Weekly = new UsageMetric { Percent = 9 } };
            var moved = UsageInterface.Merge(first, weekly9, "2|11|5|1|x", t2);
            Check(moved.FiveHour.Percent == 0 && moved.FiveHour.Observed == t2 && moved.FiveHour.Reset == UsageInterface.FiveHourEstimate, "身份变化不得沿用旧值，只见周值时按 0% 估算");
            var none = UsageInterface.Merge(null, weekly9, "2|10|5|1|x", t1);
            Check(none.FiveHour.Percent == 0 && none.FiveHour.Reset == UsageInterface.FiveHourEstimate && none.Weekly.Percent == 9, "无记录只见周值时五小时按 0%");
            var expired = UsageInterface.Merge(first, weekly9, "2|10|5|1|x", t1.AddHours(5));
            Check(expired.FiveHour.Percent == 0 && expired.FiveHour.Observed == t1.AddHours(5), "同进程超过 5 小时的五小时值按 0%");
            Check(UsageInterface.Merge(first, new UsageRecord { Status = UsageInterface.Observed }, "2|11|5|1|x", t2).FiveHour == null && UsageInterface.Merge(null, first, "2|10|5|1|x", t2).Weekly == null, "没有周值时不估算五小时，也不编造周值");
            Check(UsageInterface.Merge(second, new UsageRecord { Status = "界面读取未完成" }, "2|10|5|1|x", t2) == second && UsageInterface.Merge(second, new UsageRecord { Status = "多个用量界面，待核对" }, "2|10|5|1|x", t2).Status != UsageInterface.Observed, "暂不可读保留旧值，不确定结果不沿用");
            var restarted = UsageInterface.Merge(second, new UsageRecord { Status = UsageInterface.Observed, FiveHour = new UsageMetric { Percent = 45 } }, "2|11|5|1|x", t2);
            Check(restarted.FiveHour.Percent == 45 && restarted.Weekly == null, "重启前的周值不得充当当前额度");
            var estimateTrend = new List<TrendPoint>(); UsageTrend.Observe(estimateTrend, 2, none, t1.ToUniversalTime());
            Check(estimateTrend.Count == 1 && estimateTrend[0].Five == null && estimateTrend[0].Week == 9, "估算零值不得作为实测写入趋势");
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
