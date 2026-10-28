using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClaudeLauncher {
    public static class ProfileDeletion {
        public static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        public static string[] Candidates(int number, string roaming, string local) {
            Core.Folder(number, roaming);
            string package = Path.Combine(local, @"Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming");
            string name = number == 1 ? "Claude" : Path.GetFileName(Core.Folder(number, roaming));
            var paths = new List<string> { Path.Combine(roaming, name), Path.Combine(package, name) };
            if (number == 1) paths.Add(Path.Combine(local, @"Claude-Data\claude_pzs8sxrjxfjjc"));
            return paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        static bool Exists(string path) {
            try {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("遇到链接或重定向目录，已停止：" + path);
                if ((attributes & FileAttributes.Directory) == 0) throw new IOException("账号路径不是文件夹：" + path);
                return true;
            } catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        public static void ValidateTarget(int number, string roaming, string local, string target) {
            string full = Path.GetFullPath(target);
            if (!Candidates(number, roaming, local).Contains(full, StringComparer.OrdinalIgnoreCase))
                throw new IOException("目标不属于选定账号，已停止。");
            if (full.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(full)).DriveType != DriveType.Fixed)
                throw new IOException("仅支持本地固定磁盘上的账号目录，已停止。");
            for (DirectoryInfo parent = new DirectoryInfo(full); parent != null; parent = parent.Parent) {
                try {
                    if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("路径含链接或重定向，已停止：" + parent.FullName);
                } catch (DirectoryNotFoundException) { } catch (FileNotFoundException) { }
            }
            if (!Exists(full)) return;
            // 账号内容常有超过 260 字符的缓存路径，遍历用扩展长度前缀。
            var directories = new Stack<string>(); directories.Push(Storage.Extended(full));
            while (directories.Count > 0) {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directories.Pop())) {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("账号内容含链接，未删除任何尚未处理的目录：" + Storage.Display(entry));
                    if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                }
            }
        }
        public static string[] Prepare(int number, string roaming, string local) {
            string[] all = Candidates(number, roaming, local);
            foreach (string path in all) ValidateTarget(number, roaming, local, path);
            return all.Where(Exists).ToArray();
        }
        public static bool BlocksDeletion(int number, string command, string[] candidates) {
            if (String.IsNullOrEmpty(command)) throw new IOException("无法读取 Claude 进程信息，已停止删除。");
            Match match = Regex.Match(command, "(?:^|\\s)--user-data-dir(?:=|\\s+)(?:\"([^\"]+)\"|(\\S+))");
            if (!match.Success) {
                if (command.Contains("--user-data-dir")) throw new IOException("无法识别 Claude 目录参数，已停止删除。");
                // 无目录参数的辅助进程无法安全归属，宁可阻止删除。
                return number == 1 || Regex.IsMatch(command, @"(?:^|\s)--type=");
            }
            string directory = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Replace('/', '\\').TrimEnd('\\');
            return candidates.Contains(directory, StringComparer.OrdinalIgnoreCase);
        }
        public static void EnsureStopped(int number) {
            string[] candidates = Candidates(number, Core.Root, Local);
            // 删除检查包含辅助进程及其他会话，避免主窗口退出后仍有写入者。
            using (var query = new ManagementObjectSearcher("SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE Name='claude.exe'"))
            using (var rows = query.Get()) {
                foreach (ManagementObject row in rows) using (row) {
                    string executable = row["ExecutablePath"] as string;
                    if (String.IsNullOrEmpty(executable)) throw new IOException("有 Claude 进程无法检查，请先正常退出后再删除。");
                    if (Core.IsDesktop(executable) && BlocksDeletion(number, row["CommandLine"] as string, candidates))
                        throw new IOException("账号 " + number.ToString("00") + " 仍在运行。请在对应 Claude 窗口或托盘中正常退出，再删除；仅关闭窗口可能仍在后台运行。");
                }
            }
        }
        public static int Execute(int number, string roaming, string local, string[] approved, Action ensureStopped, Action<string> recycle) {
            ensureStopped();
            string[] actual = Prepare(number, roaming, local);
            if (!actual.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(approved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new IOException("确认后目录列表发生变化，请重新检查并确认，未继续删除。");
            if (actual.Length == 0) return 0;
            int completed = 0;
            try {
                foreach (string path in actual) {
                    ensureStopped();
                    ValidateTarget(number, roaming, local, path);
                    recycle(path);
                    if (Exists(path)) throw new IOException("目录仍然存在：" + path);
                    completed++;
                }
                if (Candidates(number, roaming, local).Any(Exists)) throw new IOException("操作期间目录被重新创建，请确认 Claude 已退出。");
                return completed;
            } catch (Exception error) {
                throw new IOException("删除未完整完成。已处理 " + completed + "/" + actual.Length + " 个目录；请检查后重试。不会自动重新创建账号。\n" + error.Message, error);
            }
        }
    }

    public static class DeletionJournal {
        static string Marker(int number, string local) {
            Core.Folder(number, local);
            string path = Path.GetFullPath(Path.Combine(local, @"Claude-MultiLauncher\pending-" + number.ToString("00")));
            for (string entry = path; entry != null; entry = Path.GetDirectoryName(entry)) {
                try {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("删除状态路径含链接，已停止：" + entry);
                } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            }
            return path;
        }
        public static bool IsPending(int number, string local) {
            string path = Marker(number, local);
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        public static void Begin(int number, string local) {
            string path = Marker(number, local);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough)) {
                file.WriteByte(1); file.Flush(true);
            }
        }
        public static void Clear(int number, string local) { File.Delete(Marker(number, local)); }
    }

    // 使用 Windows 回收站 API，不提供永久删除或失败后强制删除的退路。
    public static class RecycleBin {
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem { }
        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOperation {
            void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie);
            void SetOperationFlags(uint flags); void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            void SetProgressDialog(IntPtr dialog); void SetProperties(IntPtr properties); void SetOwnerWindow(IntPtr owner);
            void ApplyPropertiesToItem(IShellItem item); void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
            void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
            void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
            void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
            void DeleteItem(IShellItem item, IntPtr sink); void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
            void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
            void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid, out IShellItem item);
        public static void Move(string path) {
            IFileOperation operation = null; IShellItem item = null;
            try {
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
                // 静默、无自动错误对话框、遇错停止、明确送入回收站、记录撤销。
                operation.SetOperationFlags(0x4 | 0x10 | 0x400 | 0x80000 | 0x100000 | 0x20000000);
                Guid iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, ref iid, out item);
                operation.DeleteItem(item, IntPtr.Zero); operation.PerformOperations();
                bool aborted; operation.GetAnyOperationsAborted(out aborted);
                if (aborted || Directory.Exists(path)) throw new IOException("移入回收站失败或被取消；没有改用永久删除。");
            } finally {
                if (item != null) Marshal.FinalReleaseComObject(item);
                if (operation != null) Marshal.FinalReleaseComObject(operation);
            }
        }
    }
}
