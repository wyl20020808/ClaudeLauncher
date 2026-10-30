using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeLauncher {
    // Number 0 表示未管理目录的官方主进程；辅助进程不进入列表。
    public sealed class Instance {
        public int Number, ProcessId;
        public string Executable;
        public DateTime Started;
    }
    // 仅记录本次由启动器启动并带 CLAUDE_CONFIG_DIR 的进程；不读取进程环境变量。
    public sealed class LaunchRecord {
        public int ProcessId;
        public DateTime Requested;
    }

    public static class Core {
        public static string Root { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        public static string Folder(int number, string root) {
            if (number < 1 || number > 10) throw new ArgumentOutOfRangeException("number");
            return number == 1 ? null : Path.Combine(root, number == 2 ? "Claude-Second" : "Claude-Profile-" + number.ToString("00"));
        }
        public static bool IsDesktop(string path) {
            return !String.IsNullOrEmpty(path) && Regex.IsMatch(path,
                @"\b(WindowsApps\\Claude_[^\\]+\\app|AnthropicClaude(?:\\app-[^\\]+)?|Programs\\Claude|Program Files(?: \(x86\))?\\Claude)\\Claude\.exe$", RegexOptions.IgnoreCase);
        }
        public static int NumberFromCommand(string command, string root) {
            return NumberFromCommand(command, root, ProfileDeletion.Local);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string line, out int count);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        public static string[] SplitCommand(string command) {
            int count;
            IntPtr argv = CommandLineToArgvW(command, out count);
            if (argv == IntPtr.Zero) throw new InvalidOperationException("无法解析 Claude 启动参数，已停止操作。");
            try {
                var result = new string[count];
                for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                return result;
            } finally { LocalFree(argv); }
        }
        // 返回 1～10；0 表示辅助进程或未管理目录。重复、缺值、相对路径一律拒绝。
        public static int NumberFromCommand(string command, string root, string local) {
            if (String.IsNullOrEmpty(command)) throw new InvalidOperationException("无法读取 Claude 启动信息，请关闭管理员权限的 Claude 后重试。");
            string[] args = SplitCommand(command);
            if (args.Skip(1).Any(a => a.StartsWith("--type=", StringComparison.Ordinal))) return 0;
            string directory = null; int found = 0;
            for (int i = 1; i < args.Length; i++) {
                if (args[i].StartsWith("--user-data-dir=", StringComparison.Ordinal)) directory = args[i].Substring("--user-data-dir=".Length);
                else if (args[i] == "--user-data-dir") directory = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
                else continue;
                found++;
            }
            if (found == 0) return 1;
            if (found > 1 || !Regex.IsMatch(directory, @"^[A-Za-z]:[\\/]"))
                throw new InvalidOperationException("无法识别 Claude 的账号目录参数，已停止操作。");
            directory = directory.Replace('/', '\\').TrimEnd('\\');
            for (int i = 1; i <= 10; i++)
                if (ProfileDeletion.Candidates(i, root, local).Contains(directory, StringComparer.OrdinalIgnoreCase)) return i;
            return 0;
        }
        public static string CodeConfig(string folder) { return Path.Combine(folder, "ClaudeCodeConfig"); }
        public static string[] InstallationCandidates(string local, string programFiles, string programFilesX86) {
            return new[] {
                Path.Combine(local, @"Microsoft\WindowsApps\claude-desktop.exe"),
                Path.Combine(local, @"AnthropicClaude\Claude.exe"),
                Path.Combine(local, @"Programs\Claude\Claude.exe"),
                Path.Combine(programFiles, @"Claude\Claude.exe"),
                Path.Combine(programFilesX86, @"Claude\Claude.exe")
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        public static List<Instance> Scan() {
            var result = new List<Instance>();
            int session = Process.GetCurrentProcess().SessionId;
            using (var query = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE Name='claude.exe' AND SessionId=" + session))
            using (var rows = query.Get()) {
                foreach (ManagementObject row in rows) using (row) {
                    string path = row["ExecutablePath"] as string;
                    if (String.IsNullOrEmpty(path)) throw new InvalidOperationException("无法检查一个 Claude 进程。请用普通用户权限运行 Claude 和启动器。");
                    if (!IsDesktop(path)) continue;
                    string command = row["CommandLine"] as string;
                    if (command != null && IsHelper(command)) continue;
                    int number = NumberFromCommand(command, Root);
                    string created = row["CreationDate"] as string;
                    result.Add(new Instance { Number = number, ProcessId = Convert.ToInt32(row["ProcessId"]), Executable = path,
                        Started = created == null ? DateTime.MinValue : ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime() });
                }
            }
            return result;
        }
        public static bool IsHelper(string command) {
            return SplitCommand(command).Skip(1).Any(a => a.StartsWith("--type=", StringComparison.Ordinal));
        }
        public static bool Running(int number, IEnumerable<Instance> running) { return running.Any(x => x.Number == number); }
        // 界面只显示本机已有账号：目录存在、运行中或正在启动/检查。
        public static bool Visible(int number, IEnumerable<Instance> running, IEnumerable<int> busy, Func<int, bool> hasData) {
            return Running(number, running) || busy.Contains(number) || hasData(number);
        }
        // “新建实例”取 01～10 中最小的未使用编号；0 表示全部已用。
        public static int NextFree(IEnumerable<Instance> running, IEnumerable<int> busy, Func<int, bool> hasData) {
            for (int n = 1; n <= 10; n++) if (!Visible(n, running, busy, hasData)) return n;
            return 0;
        }
        // 只有启动器本次启动、且此后仍是同一存活进程时，才显示“已指定”。
        public static string CodeState(int number, IEnumerable<Instance> running, LaunchRecord record) {
            if (number == 1) return "Code 默认";
            var found = running.Where(x => x.Number == number).ToList();
            if (found.Count == 0) return "Code 待启动";
            return found.Count == 1 && record != null && found[0].ProcessId == record.ProcessId && found[0].Started >= record.Requested ? "Code 已指定" : "Code 未确认";
        }
        public static string DeleteBlocker(int number, IEnumerable<Instance> running, bool busy, int protectedNumber) {
            if (number == 1) return "账号 01 是 Claude 默认目录，启动器不删除它。";
            if (number < 2 || number > 10) return "账号编号无效。";
            if (protectedNumber == number) return "该账号处于登录保护中，请先结束保护。";
            if (busy || Running(number, running)) return "该账号正在运行、启动或检查中。请先在 Claude 中正常退出（包括托盘图标），再删除。";
            return null;
        }
        public static string LoginBlocker(int number, IEnumerable<Instance> running, IEnumerable<int> busy) {
            if (running.Any(x => x.Number != number)) return "请先保存工作并正常退出其他 Claude 实例（包括未管理的实例和托盘中的实例），再进入登录保护。启动器不会替你退出或登出账号。";
            if (running.Count(x => x.Number == number) > 1) return "该账号有多个主进程，不能进入登录保护。";
            if (busy.Any(x => x != number)) return "其他账号正在检查或启动，请等待完成后重试。";
            return null;
        }
        // expectedPid 为后台检查前核实的 PID（0 表示当时未运行）；检查后它退出或被替换时不自动重启、不切换 PID。
        public static string OpeningBlocker(int number, int expectedPid, IEnumerable<Instance> running, int protectedNumber, IEnumerable<int> busy) {
            var found = running.Where(x => x.Number == number).ToList();
            if (expectedPid != 0 && (found.Count != 1 || found[0].ProcessId != expectedPid)) return "检查期间原实例已退出或被替换，未自动重启。请重新点击。";
            if (protectedNumber != 0 && (protectedNumber != number || LoginBlocker(number, running, busy) != null))
                return "登录保护中：请先完成目标账号登录并核对身份。外部启动的其他 Claude 需要你正常退出。";
            if (found.Count > 1) return "同一目录发现多个主进程，未激活或结束任何实例。请自行检查 Claude 窗口。";
            return null;
        }
        public static string FindExecutable(List<Instance> running) {
            string existing = running.Select(x => x.Executable).FirstOrDefault(File.Exists);
            if (existing != null) return existing;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string path in InstallationCandidates(local, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))) {
                if (File.Exists(path)) return path;
            }
            // 商店安装可能关闭了执行别名；只读取当前用户的包安装位置。
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; (Get-AppxPackage -Name Claude | Sort-Object Version -Descending | Select-Object -First 1).InstallLocation\"");
            info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            using (var process = Process.Start(info)) {
                if (!process.WaitForExit(8000)) { process.Kill(); throw new InvalidOperationException("查找 Claude 超时，请确认已安装桌面版，再点击刷新。"); }
                string location = process.StandardOutput.ReadToEnd().Trim();
                if (process.ExitCode == 0 && location.Length > 0) {
                    string path = Path.Combine(location, @"app\Claude.exe");
                    if (File.Exists(path)) return path;
                }
            }
            throw new InvalidOperationException("未找到 Claude 桌面版。请先安装，再点击右上角刷新。");
        }
        public static bool HasData(int number) {
            return ProfileDeletion.Candidates(number, Root, ProfileDeletion.Local).Any(Directory.Exists);
        }
        // 递归存储检查已在后台完成；这里只复查目录链与顶层存储项（含新建的 ClaudeCodeConfig），返回启动的 PID。
        public static int Launch(int number, string executable) {
            Diagnostics.Write("account_launch", "slot=" + number + "; claude=" + executable);
            if (DeletionJournal.IsPending(number, ProfileDeletion.Local)) throw new IOException("该账号删除未完成，请先重试删除。");
            Storage.Validate(number, Root, ProfileDeletion.Local, false);
            var info = PrepareLaunch(number, executable, Root);
            Storage.Validate(number, Root, ProfileDeletion.Local, false);
            using (var process = Process.Start(info)) return process.Id;
        }
        // 菜单中显示/复制的目录：优先已存在的候选目录。
        public static string DisplayFolder(int number) {
            string[] candidates = ProfileDeletion.Candidates(number, Root, ProfileDeletion.Local);
            string existing = candidates.FirstOrDefault(Directory.Exists);
            return existing ?? (number == 1 ? candidates.Last() : Folder(number, Root));
        }
        public static ProcessStartInfo PrepareLaunch(int number, string executable, string root) {
            string folder = Folder(number, root);
            var info = new ProcessStartInfo(executable, folder == null ? "" : "--user-data-dir=\"" + folder + "\"");
            // 设置子进程环境变量需要 UseShellExecute=false。
            info.UseShellExecute = false;
            if (folder != null) {
                Directory.CreateDirectory(CodeConfig(folder));
                info.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = CodeConfig(folder);
            } else info.EnvironmentVariables.Remove("CLAUDE_CONFIG_DIR"); // 01 用官方默认，不继承启动器自身的变量。
            return info;
        }
        public static bool NeedsLaunch(int number, IEnumerable<Instance> running) {
            Folder(number, Root);
            return !running.Any(x => x.Number == number);
        }
        // 该账号唯一的已核实主进程；未运行返回 null；多个主进程时报错，不结束任何进程。
        public static Instance Single(int number, IEnumerable<Instance> running) {
            var found = running.Where(x => x.Number == number).ToList();
            if (found.Count > 1) throw new InvalidOperationException("账号 " + number.ToString("00") + " 有多个 Claude 主进程（PID " + String.Join("、", found.Select(x => x.ProcessId)) + "），已停止操作，未激活或结束任何实例。");
            return found.FirstOrDefault();
        }
    }

    // 登录保护的账号编号保存在启动器自身目录，重开启动器仍保留；不保存任何身份信息。
    public static class LoginProtection {
        static string File(string local) { return Path.Combine(local, "Claude-MultiLauncher", "login-protection"); }
        public static int Read(string local) {
            try {
                int number;
                return Int32.TryParse(System.IO.File.ReadAllText(File(local)).Trim(), out number) && number >= 1 && number <= 10 ? number : 0;
            } catch (FileNotFoundException) { return 0; } catch (DirectoryNotFoundException) { return 0; }
        }
        public static void Save(string local, int number) {
            string path = File(local);
            if (number == 0) { System.IO.File.Delete(path); return; }
            Core.Folder(number, local);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, number.ToString());
        }
    }

    public static class Native {
        delegate bool EnumProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        public static IntPtr WindowFor(int processId) {
            IntPtr found = IntPtr.Zero; long area = -1;
            EnumWindows(delegate(IntPtr window, IntPtr unused) {
                uint process; GetWindowThreadProcessId(window, out process);
                if (process == processId && IsWindowVisible(window) && GetWindowTextLength(window) > 0) {
                    Rect rect;
                    if (GetWindowRect(window, out rect)) {
                        long size = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                        if (size > area) { area = size; found = window; }
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        public static bool IsMinimized(IntPtr window) { return window != IntPtr.Zero && IsIconic(window); }
        public static int ForegroundProcess() { uint process; GetWindowThreadProcessId(GetForegroundWindow(), out process); return (int)process; }
        public static bool Focus(IntPtr window) {
            if (window == IntPtr.Zero) return false;
            ShowWindow(window, IsIconic(window) ? 9 : 5);
            return SetForegroundWindow(window);
        }
    }

    public static class Program {
        // 再次打开启动器时通知已运行的实例显示窗口（窗口可能隐藏在通知区域）。
        public static string ShowSignalName { get { return @"Local\ClaudeProfileLauncher-show-" + WindowsIdentity.GetCurrent().User.Value; } }
        [STAThread]
        public static int Main(string[] args) {
            Diagnostics.Write("startup", "os=" + Environment.OSVersion + "; runtime=" + Environment.Version);
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { Diagnostics.Error("unhandled_exception", e.ExceptionObject as Exception); };
            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e) { Diagnostics.Error("unobserved_task", e.Exception); e.SetObserved(); };
            AppDomain.CurrentDomain.ProcessExit += delegate { Diagnostics.Write("process_exit", "exitCode=" + Environment.ExitCode); };
            if (args.Length == 1 && args[0] == "--apply-update") return Updates.Install();
            if (args.Length == 1 && args[0] == "--self-test") return SelfTest.Entry();
            if (args.Length == 1 && args[0] == "--usage-interface-check") return UsageInterface.Check();
            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = new Mutex(false, @"Local\ClaudeProfileLauncher-" + sid)) {
                bool owns;
                try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) {
                    Diagnostics.Write("existing_launcher", "复用已打开的启动器");
                    try { using (var signal = EventWaitHandle.OpenExisting(ShowSignalName)) signal.Set(); return 0; }
                    catch (WaitHandleCannotBeOpenedException) { } // 旧版本没有该信号，退回按标题查找窗口。
                    for (int i = 0; i < 15; i++) {
                        IntPtr existing = Native.FindWindow(null, LauncherUI.Title);
                        if (existing != IntPtr.Zero) { Native.Focus(existing); return 0; }
                        Thread.Sleep(150);
                    }
                    return 0;
                }
                try {
                    var app = new Application();
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { Diagnostics.Error("ui_unhandled_exception", e.Exception); };
                    var ui = new LauncherUI();
                    app.Dispatcher.BeginInvoke(new Action(() => { if (args.Length == 2 && args[0] == "--update-ready") Updates.Ready(args[1]); }));
                    ui.Show(); return 0;
                }
                catch (Exception error) { Diagnostics.Error("fatal", error); MessageBox.Show(error.Message + "\n日志：" + Diagnostics.DirectoryPath, "Claude 启动器", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
