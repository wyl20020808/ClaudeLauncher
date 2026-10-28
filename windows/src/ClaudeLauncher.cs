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
    public sealed class Instance {
        public int Number, ProcessId;
        public string Executable;
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
            using (var query = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='claude.exe' AND SessionId=" + session))
            using (var rows = query.Get()) {
                foreach (ManagementObject row in rows) using (row) {
                    string path = row["ExecutablePath"] as string;
                    if (String.IsNullOrEmpty(path)) throw new InvalidOperationException("无法检查一个 Claude 进程。请用普通用户权限运行 Claude 和启动器。");
                    if (!IsDesktop(path)) continue;
                    int number = NumberFromCommand(row["CommandLine"] as string, Root);
                    if (number > 0) result.Add(new Instance { Number = number, ProcessId = Convert.ToInt32(row["ProcessId"]), Executable = path });
                }
            }
            return result;
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
        public static void Launch(int number, string executable) {
            Diagnostics.Write("account_launch", "slot=" + number + "; claude=" + executable);
            if (DeletionJournal.IsPending(number, ProfileDeletion.Local)) throw new IOException("该账号删除未完成，请先重试删除。");
            Storage.Validate(number, Root, ProfileDeletion.Local);
            using (var process = Process.Start(PrepareLaunch(number, executable, Root))) { }
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

    public static class Native {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void DarkTitleBar(Window window) {
            try {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                int enabled = 1, caption = 0x00262525, text = 0x00CCCCCC;
                DwmSetWindowAttribute(handle, 20, ref enabled, 4);
                DwmSetWindowAttribute(handle, 35, ref caption, 4);
                DwmSetWindowAttribute(handle, 36, ref text, 4);
            }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
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
        public static bool Focus(IntPtr window) {
            if (window == IntPtr.Zero) return false;
            ShowWindow(window, IsIconic(window) ? 9 : 5);
            return SetForegroundWindow(window);
        }
    }

    public sealed class LauncherUI {
        public const string Title = "Claude 启动器";
        readonly Window window;
        readonly TextBlock status, count;
        readonly Button refresh, update;
        readonly Dictionary<int, Button> buttons = new Dictionary<int, Button>();
        readonly Dictionary<int, Button> deleteButtons = new Dictionary<int, Button>();
        readonly HashSet<int> incompleteDeletes = new HashSet<int>();
        readonly Dictionary<int, TextBlock> labels = new Dictionary<int, TextBlock>();
        readonly Dictionary<int, DateTime> pending = new Dictionary<int, DateTime>();
        readonly DispatcherTimer timer = new DispatcherTimer();
        List<Instance> instances = new List<Instance>();
        string executable;
        bool scanning, closed, healthy, deleting, updating;
        Release available;
        string lastProcessState = "", lastRefreshError = "";
        readonly ProcessWatch processWatch = new ProcessWatch();
        DateTime nextHeartbeat = DateTime.MinValue;
        static Brush Color(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }

        public LauncherUI() {
            for (int i = 1; i <= 10; i++) if (DeletionJournal.IsPending(i, ProfileDeletion.Local)) incompleteDeletes.Add(i);
            window = (Window)XamlReader.Parse(Layout);
            window.SourceInitialized += delegate { Native.DarkTitleBar(window); };
            using (Stream icon = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeLauncher.Icon")) {
                if (icon == null) throw new InvalidOperationException("启动器缺少内嵌图标。");
                window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(icon,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            }
            window.Height = Math.Min(820, Math.Max(560, SystemParameters.WorkArea.Height - 48));
            status = (TextBlock)window.FindName("Status"); count = (TextBlock)window.FindName("Count");
            refresh = (Button)window.FindName("Refresh");
            update = (Button)window.FindName("Update");
            ((TextBlock)window.FindName("Version")).Text = "v" + Updates.Version;
            ((Button)window.FindName("Logs")).Click += delegate {
                try { Diagnostics.OpenFolder(); } catch (Exception error) { Diagnostics.Error("open_logs_error", error); Message("无法打开日志目录：" + error.Message, true); }
            };
            update.Click += async delegate { await UpdateClick(); };
            var grid = (UniformGrid)window.FindName("Accounts");
            for (int i = 1; i <= 10; i++) {
                int number = i;
                var content = new Grid { Margin = new Thickness(0, 0, 76, 0) };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                var index = new TextBlock { Text = i.ToString("00"), FontFamily = new FontFamily("Consolas"), Foreground = Color("#858585"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(index);
                var name = new TextBlock { Text = i == 1 ? "默认账号" : "账号 " + i.ToString("00"), FontSize = 14, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1); content.Children.Add(name);
                var label = new TextBlock { Text = "正在检查…", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(label, 2);
                content.Children.Add(label);
                var button = new Button { Content = content, Style = (Style)window.FindResource("AccountCard"), IsEnabled = false };
                AutomationProperties.SetAutomationId(button, "Profile" + number);
                button.Click += async delegate { await Open(number); };
                var delete = new Button { Content = "删除", Style = (Style)window.FindResource("DeleteButton"),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0), IsEnabled = false };
                AutomationProperties.SetAutomationId(delete, "DeleteProfile" + number);
                AutomationProperties.SetName(delete, "删除账号 " + number.ToString("00") + " 的本地数据");
                delete.ToolTip = "删除本机数据，保留云端账号与聊天记录";
                delete.Click += async delegate { await Delete(number); };
                var card = new Grid(); card.Children.Add(button); card.Children.Add(delete);
                buttons[number] = button; deleteButtons[number] = delete; labels[number] = label; grid.Children.Add(card);
            }
            refresh.Click += async delegate { executable = null; await Refresh(); };
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += async delegate { await Refresh(); };
            window.Loaded += async delegate { await Refresh(); timer.Start(); await CheckUpdate(false); };
            window.Closed += delegate { closed = true; timer.Stop(); processWatch.Dispose(); Diagnostics.Write("window_closed", "正常关闭启动器"); };
            window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (deleting) { e.Cancel = true; Message("请先完成或取消当前删除操作。", true); }
            };
        }
        void Message(string message, bool error) {
            status.Text = message; status.Foreground = Color(error ? "#F48771" : "#CCCCCC");
        }
        void Render() {
            count.Text = instances.Select(x => x.Number).Distinct().Count() + " 个运行中";
            foreach (var pair in buttons) {
                int n = pair.Key;
                bool running = instances.Any(x => x.Number == n);
                string text = incompleteDeletes.Contains(n) ? "删除未完成 · 请重试删除" : pending.ContainsKey(n) ? "正在打开，请稍候…" :
                    !healthy ? "暂时不可用 · 请查看下方提示" :
                    running ? "运行中 · 点击切回" : Core.HasData(n) ? "未运行 · 点击启动" : "首次使用 · 点击创建";
                labels[n].Text = text;
                labels[n].Foreground = Color(running && healthy ? "#89D185" : "#A8A8A8");
                pair.Value.IsEnabled = healthy && !deleting && !updating && !pending.ContainsKey(n) && !incompleteDeletes.Contains(n);
                // 删除功能按规格暂停；仅为未完成的旧删除保留重试入口，避免账号被永久卡住。
                deleteButtons[n].Visibility = incompleteDeletes.Contains(n) ? Visibility.Visible : Visibility.Collapsed;
                deleteButtons[n].IsEnabled = healthy && !deleting && !updating && pending.Count == 0 && incompleteDeletes.Contains(n);
                AutomationProperties.SetName(pair.Value, "账号 " + n.ToString("00") + "，" + text);
            }
        }
        async Task Refresh() {
            if (scanning || closed || deleting) return;
            scanning = true; refresh.IsEnabled = false;
            try {
                var latest = await Task.Run(() => Core.Scan());
                string found = executable;
                if (String.IsNullOrEmpty(found) || !File.Exists(found)) found = await Task.Run(() => Core.FindExecutable(latest));
                if (closed) return;
                processWatch.Refresh(latest);
                if (DateTime.UtcNow >= nextHeartbeat) { Diagnostics.Write("heartbeat", "accounts=" + latest.Count); nextHeartbeat = DateTime.UtcNow.AddSeconds(30); }
                string processState = String.Join(",", latest.OrderBy(x => x.Number).ThenBy(x => x.ProcessId).Select(x => x.Number + ":" + x.ProcessId));
                if (processState != lastProcessState) {
                    foreach (var gone in instances.Where(x => !latest.Any(y => y.ProcessId == x.ProcessId))) Diagnostics.Write("account_process_gone", "slot=" + gone.Number + "; pid=" + gone.ProcessId + "; reason=unknown (可能正常退出，不能据此判定崩溃)");
                    Diagnostics.Write("account_process_state", processState); lastProcessState = processState;
                }
                instances = latest; executable = found;
                lastRefreshError = "";
                if (!healthy) Message("选择一个账号。已运行的账号会直接切回原窗口。", false);
                healthy = true;
                foreach (int number in pending.Keys.ToArray()) {
                    IntPtr handle = instances.Where(x => x.Number == number).Select(x => Native.WindowFor(x.ProcessId)).FirstOrDefault(x => x != IntPtr.Zero);
                    if (handle != IntPtr.Zero) {
                        pending.Remove(number);
                        // 用户已切到其他应用时，不再抢占焦点。
                        if (window.IsActive) Native.Focus(handle);
                        Message("账号 " + number.ToString("00") + " 已打开。首次使用请在 Claude 中登录。", false);
                    } else if (DateTime.UtcNow - pending[number] > TimeSpan.FromSeconds(35)) {
                        pending.Remove(number);
                        Message(instances.Any(x => x.Number == number) ? "账号 " + number.ToString("00") + " 已在运行但尚未显示窗口；可能在托盘中，请稍后重试。" :
                            "账号 " + number.ToString("00") + " 启动失败：35 秒内未出现 Claude 主进程。", true);
                    }
                }
            } catch (Exception error) {
                if (closed) return;
                if (lastRefreshError != error.Message) { Diagnostics.Error("refresh_error", error); lastRefreshError = error.Message; }
                healthy = false; Message(error.Message, true);
            } finally {
                scanning = false;
                if (!closed) { refresh.IsEnabled = true; Render(); }
            }
        }
        async Task Open(int number) {
            if (!healthy || deleting || updating || incompleteDeletes.Contains(number) || pending.ContainsKey(number)) return;
            Diagnostics.Write("account_click", "slot=" + number);
            pending[number] = DateTime.UtcNow; Render();
            Message("正在打开账号 " + number.ToString("00") + "…", false);
            try {
                var latest = await Task.Run(() => Core.Scan());
                if (closed) return;
                Instance running = Core.Single(number, latest);
                IntPtr handle = running == null ? IntPtr.Zero : Native.WindowFor(running.ProcessId);
                if (handle != IntPtr.Zero) {
                    bool focused = Native.Focus(handle);
                    Diagnostics.Write("account_reuse", "slot=" + number + "; focused=" + focused);
                    pending.Remove(number); instances = latest;
                    Message(focused ? "已切回账号 " + number.ToString("00") + "，没有重复启动。" : "账号已经运行；Windows 未允许切换焦点，请从任务栏打开。", !focused);
                } else if (running == null) {
                    string path = executable;
                    await Task.Run(() => Core.Launch(number, path));
                } else {
                    // SPEC Windows 例外：托盘实例无可见窗口时，用该 PID 自身的程序同目录再启动，由 Claude 单实例锁转交窗口。
                    Diagnostics.Write("account_tray_relaunch", "slot=" + number + "; pid=" + running.ProcessId + "; claude=" + running.Executable);
                    await Task.Run(() => Core.Launch(number, running.Executable));
                    // shortcut: 固定等 5 秒让转交进程退出后复查一次；若出现慢退出的误报，再改为多次复查。
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    var after = await Task.Run(() => Core.Scan());
                    if (closed) return;
                    instances = after;
                    Core.Single(number, after);
                }
            } catch (Exception error) { Diagnostics.Error("account_open_error", error); pending.Remove(number); Message(error.Message, true); }
            if (!closed) Render();
        }
        public void Show() { window.ShowDialog(); }
        async Task CheckUpdate(bool manual) {
            update.IsEnabled = false;
            try {
                var release = await Task.Run(() => Updates.Check());
                if (closed) return;
                available = Updates.IsNewer(release) ? release : null;
                update.Content = available == null ? "检查更新" : "更新到 " + available.version;
                if (manual) Message(available == null ? "当前已是最新发布版本。" : "发现新版。再次点击更新按钮可下载并安装。", false);
            } catch (Exception error) {
                Diagnostics.Error("update_check_error", error);
                if (manual && !closed) Message("暂时无法检查更新；可以继续使用。" + error.Message, true);
            } finally { if (!closed) update.IsEnabled = true; }
        }
        async Task UpdateClick() {
            if (updating || deleting || pending.Count > 0) { Message("请先等待当前操作完成。", true); return; }
            if (available == null) { await CheckUpdate(true); return; }
            if (MessageBox.Show(window, "下载并更新启动器到 " + available.version + "？\n仅重启启动器，不退出 Claude、不改动账号数据。", "更新启动器", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            updating = true; update.IsEnabled = false; Render();
            try {
                Message("正在下载并校验更新签名…", false);
                string staged = await Task.Run(() => Updates.Download(available));
                if (closed) return;
                Updates.StartInstaller(staged);
                Diagnostics.Write("update_handoff", "退出启动器等待替换"); window.Close();
            } catch (Exception error) { Diagnostics.Error("update_error", error); if (!closed) Message("更新未完成，原版仍可使用：" + error.Message, true); }
            finally { updating = false; if (!closed) { update.IsEnabled = true; Render(); } }
        }

        bool ConfirmDelete(int number, string[] paths) {
            Window dialog = BuildDeleteDialog(number, paths);
            dialog.Owner = window;
            return dialog.ShowDialog() == true;
        }
        internal Window BuildDeleteDialog(int number, string[] paths) {
            var dialog = new Window { Title = "删除账号 " + number.ToString("00") + " 的本地数据", Width = 620, SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Max(450, SystemParameters.WorkArea.Height - 60), WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, Background = Color("#252526"), Foreground = Color("#CCCCCC"), FontFamily = window.FontFamily, FontSize = 14 };
            dialog.Resources = window.Resources;
            dialog.SourceInitialized += delegate { Native.DarkTitleBar(dialog); };
            var panel = new StackPanel { Margin = new Thickness(24) };
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new TextBlock { Text = "删除账号 " + number.ToString("00") + " 的本地数据？", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Color("#F48771") });
            panel.Children.Add(new TextBlock { Text = "以下目录内的全部内容将移入 Windows 回收站，包括登录状态、缓存、本地配置和会话文件。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 10) });
            panel.Children.Add(new TextBox { Text = String.Join(Environment.NewLine + Environment.NewLine, paths), IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 180, Padding = new Thickness(10), Background = Color("#1E1E1E"), Foreground = Color("#CCCCCC"), BorderBrush = Color("#454545") });
            panel.Children.Add(new TextBlock { Text = "这不会注销云端账号，也不会删除云端聊天记录。删除后保留编号，再次点击即可创建空的本地账号空间。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
            var confirm = new Button { Content = "移入回收站", Padding = new Thickness(18, 8, 18, 8), Foreground = Brushes.White, Background = Color("#A1260D"), IsEnabled = number != 1 };
            AutomationProperties.SetAutomationId(confirm, "ConfirmDelete");
            if (number == 1) {
                var acknowledge = new CheckBox { Content = "我确认：01 是原有默认账号，将清除它原来的本机数据。", Margin = new Thickness(0, 4, 0, 16), Foreground = Color("#F48771") };
                acknowledge.Checked += delegate { confirm.IsEnabled = true; };
                acknowledge.Unchecked += delegate { confirm.IsEnabled = false; };
                panel.Children.Add(acknowledge);
            }
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", IsCancel = true, IsDefault = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 12, 0) };
            confirm.Click += delegate { dialog.DialogResult = true; };
            actions.Children.Add(cancel); actions.Children.Add(confirm); panel.Children.Add(actions);
            dialog.Loaded += delegate { cancel.Focus(); };
            return dialog;
        }
        async Task Delete(int number) {
            if (!healthy || deleting || updating || pending.Count > 0) return;
            deleting = true; Render();
            Message("正在检查账号 " + number.ToString("00") + " 的目录和运行状态…", false);
            bool confirmed = false;
            try {
                string[] paths = await Task.Run(() => { ProfileDeletion.EnsureStopped(number); return ProfileDeletion.Prepare(number, Core.Root, ProfileDeletion.Local); });
                if (closed) return;
                if (paths.Length == 0) { DeletionJournal.Clear(number, ProfileDeletion.Local); incompleteDeletes.Remove(number); Message("该编号已没有本地数据，可以重新创建。", false); return; }
                if (!ConfirmDelete(number, paths)) { Message("已取消删除，账号数据未改动。", false); return; }
                confirmed = true;
                DeletionJournal.Begin(number, ProfileDeletion.Local);
                Message("正在将账号 " + number.ToString("00") + " 的数据移入回收站…", false);
                var completion = new TaskCompletionSource<int>();
                var worker = new Thread(delegate() {
                    try { completion.SetResult(ProfileDeletion.Execute(number, Core.Root, ProfileDeletion.Local, paths, () => ProfileDeletion.EnsureStopped(number), RecycleBin.Move)); }
                    catch (Exception error) { completion.SetException(error); }
                });
                worker.SetApartmentState(ApartmentState.STA); worker.Start();
                await completion.Task;
                DeletionJournal.Clear(number, ProfileDeletion.Local);
                incompleteDeletes.Remove(number);
                Message("账号 " + number.ToString("00") + " 本地数据已移入回收站。编号保留，点击即可重新创建。", false);
            } catch (Exception error) {
                Diagnostics.Error("delete_error", error);
                if (confirmed) incompleteDeletes.Add(number);
                Message(error.Message, true);
            } finally { deleting = false; if (!closed) Render(); }
        }

        const string Layout = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 Title='Claude 启动器' Width='960' Height='820' MinWidth='820' MinHeight='560' Background='#1E1E1E'
 FontFamily='Segoe UI, Microsoft YaHei UI' FontSize='13' Foreground='#CCCCCC' WindowStartupLocation='CenterScreen' UseLayoutRounding='True'>
 <Window.Resources>
  <Style TargetType='Button'>
   <Setter Property='Foreground' Value='#CCCCCC'/><Setter Property='Background' Value='Transparent'/>
   <Setter Property='BorderBrush' Value='Transparent'/><Setter Property='BorderThickness' Value='1'/>
   <Setter Property='Padding' Value='12,8'/><Setter Property='Cursor' Value='Hand'/>
   <Setter Property='HorizontalContentAlignment' Value='Center'/>
   <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
    <Border x:Name='Box' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}' CornerRadius='3'>
     <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' RecognizesAccessKey='True'/>
    </Border>
    <ControlTemplate.Triggers>
     <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='Background' Value='#37373D'/></Trigger>
     <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='#007FD4'/></Trigger>
     <Trigger Property='IsPressed' Value='True'><Setter TargetName='Box' Property='Background' Value='#094771'/></Trigger>
     <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
    </ControlTemplate.Triggers>
   </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='AccountCard' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
   <Setter Property='Padding' Value='18,0'/><Setter Property='Height' Value='52'/>
   <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
   <Setter Property='BorderBrush' Value='#2D2D30'/><Setter Property='BorderThickness' Value='0,0,0,1'/>
  </Style>
  <Style x:Key='DeleteButton' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
   <Setter Property='FontSize' Value='12'/><Setter Property='Padding' Value='10,6'/><Setter Property='Foreground' Value='#A8A8A8'/>
   <Style.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter Property='Foreground' Value='#F48771'/></Trigger></Style.Triggers>
  </Style>
  <Style x:Key='SidebarButton' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
   <Setter Property='HorizontalContentAlignment' Value='Left'/><Setter Property='Padding' Value='18,11'/>
   <Setter Property='Margin' Value='8,2'/>
  </Style>
 </Window.Resources>
 <Grid>
  <Grid.RowDefinitions><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
  <Grid>
   <Grid.ColumnDefinitions><ColumnDefinition Width='190'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
   <Border Background='#252526' BorderBrush='#303031' BorderThickness='0,0,1,0'>
    <DockPanel>
     <StackPanel DockPanel.Dock='Top'>
      <StackPanel Orientation='Horizontal' Margin='20,24,12,28'>
       <Image Source='{Binding Icon, RelativeSource={RelativeSource AncestorType=Window}}' Width='25' Height='25' Margin='0,0,10,0'/>
       <TextBlock Text='Claude 启动器' FontSize='15' FontWeight='SemiBold' VerticalAlignment='Center' Foreground='#F0F0F0'/>
      </StackPanel>
      <Border Background='#37373D' BorderBrush='#007ACC' BorderThickness='2,0,0,0' Padding='24,12' Margin='0,0,0,20'>
       <TextBlock Text='账号管理' Foreground='#FFFFFF'/>
      </Border>
      <Button x:Name='Logs' Content='查看本地日志' Style='{StaticResource SidebarButton}'/>
      <Button x:Name='Update' Content='检查更新' Style='{StaticResource SidebarButton}'/>
     </StackPanel>
     <StackPanel DockPanel.Dock='Bottom' Margin='24,20'>
      <TextBlock Text='本地账号空间' Foreground='#A8A8A8' FontSize='12'/>
      <TextBlock Text='关闭启动器不会退出 Claude' Foreground='#A8A8A8' TextWrapping='Wrap' FontSize='12' Margin='0,8,0,0' LineHeight='18'/>
     </StackPanel>
     <Border/>
    </DockPanel>
   </Border>
   <Grid Grid.Column='1'>
    <Grid.RowDefinitions><RowDefinition Height='38'/><RowDefinition Height='Auto'/><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
    <Border Background='#252526' BorderBrush='#303031' BorderThickness='0,0,0,1'>
     <Border Background='#1E1E1E' BorderBrush='#007ACC' BorderThickness='0,2,0,0' HorizontalAlignment='Left' Padding='22,8'>
      <TextBlock Text='账号管理' Foreground='#F0F0F0' FontSize='12'/>
     </Border>
    </Border>
    <Grid Grid.Row='1' Margin='24,22,24,18'>
     <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
     <StackPanel>
      <TextBlock Text='账号' FontSize='23' Foreground='#F0F0F0' FontWeight='SemiBold'/>
      <TextBlock Text='选择账号启动；运行中的账号会切回原窗口。' Foreground='#A8A8A8' FontSize='12' Margin='0,8,0,0' TextWrapping='Wrap'/>
     </StackPanel>
     <Button x:Name='Refresh' Grid.Column='1' Content='刷新状态' VerticalAlignment='Top' Background='#0E639C' Foreground='White' Margin='16,0,0,0'/>
    </Grid>
    <Grid Grid.Row='2' Margin='24,0,24,0'>
     <Grid.RowDefinitions><RowDefinition Height='30'/><RowDefinition Height='*'/></Grid.RowDefinitions>
     <Border Background='#252526' BorderBrush='#303031' BorderThickness='0,0,0,1' Padding='18,0'>
      <Grid><Grid.ColumnDefinitions><ColumnDefinition Width='40'/><ColumnDefinition Width='*'/><ColumnDefinition Width='266'/></Grid.ColumnDefinitions>
       <TextBlock Text='#' Foreground='#A8A8A8' VerticalAlignment='Center' FontSize='11'/>
       <TextBlock Grid.Column='1' Text='账号名称' Foreground='#A8A8A8' VerticalAlignment='Center' FontSize='11'/>
       <TextBlock Grid.Column='2' Text='状态 / 操作' Foreground='#A8A8A8' VerticalAlignment='Center' FontSize='11'/>
      </Grid>
     </Border>
     <ScrollViewer Grid.Row='1' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>
      <UniformGrid x:Name='Accounts' Columns='1' VerticalAlignment='Top'/>
     </ScrollViewer>
    </Grid>
    <Border Grid.Row='3' Background='#252526' BorderBrush='#303031' BorderThickness='0,1,0,0' Padding='24,14' Margin='0,18,0,0'>
     <TextBlock x:Name='Status' Text='正在查找 Claude 桌面版…' TextWrapping='Wrap' FontSize='12' Foreground='#CCCCCC' MinHeight='18'/>
    </Border>
   </Grid>
  </Grid>
  <Border Grid.Row='1' Background='#007ACC' Padding='16,5'>
   <DockPanel>
    <TextBlock x:Name='Version' DockPanel.Dock='Right' Foreground='White' FontSize='11'/>
    <TextBlock Text='Windows' DockPanel.Dock='Left' Foreground='White' FontSize='11' Margin='0,0,24,0'/>
    <TextBlock x:Name='Count' Text='正在检查…' Foreground='White' FontSize='11'/>
   </DockPanel>
  </Border>
 </Grid>
</Window>";
    }

    public static class Program {
        [STAThread]
        public static int Main(string[] args) {
            Diagnostics.Write("startup", "os=" + Environment.OSVersion + "; runtime=" + Environment.Version);
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { Diagnostics.Error("unhandled_exception", e.ExceptionObject as Exception); };
            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e) { Diagnostics.Error("unobserved_task", e.Exception); e.SetObserved(); };
            AppDomain.CurrentDomain.ProcessExit += delegate { Diagnostics.Write("process_exit", "exitCode=" + Environment.ExitCode); };
            if (args.Length == 1 && args[0] == "--apply-update") return Updates.Install();
            if (args.Length == 1 && args[0] == "--self-test") return SelfTest.Entry();
            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = new Mutex(false, @"Local\ClaudeProfileLauncher-" + sid)) {
                bool owns;
                try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) {
                    Diagnostics.Write("existing_launcher", "复用已打开的启动器");
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
