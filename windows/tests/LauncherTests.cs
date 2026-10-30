using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using ClaudeLauncher;

public static class LauncherTests {
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static System.Collections.Generic.IEnumerable<DependencyObject> Children(DependencyObject root) {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root)) {
            var node = child as DependencyObject;
            if (node != null) foreach (var descendant in Children(node)) yield return descendant;
        }
    }
    [STAThread]
    public static int Main(string[] args) {
        Diagnostics.DirectoryOverride=Path.Combine(Path.GetTempPath(),"launcher-diagnostics-test-"+Guid.NewGuid().ToString("N"));
        if (args.Contains("--exit-test-probe")) { System.Threading.Thread.Sleep(600); return 17; }
        if (args.Contains("--launcher-test-probe")) {
            Console.WriteLine(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("env:" + Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"))));
            foreach (string arg in args.Where(a => a != "--launcher-test-probe")) Console.WriteLine(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(arg)));
            return 0;
        }
        // 控制台代码页无法输出中文时，未处理异常连消息都打印不出，这里转为 Base64 以保证可读。
        try { return Run(); }
        catch (Exception error) {
            Console.WriteLine("FAIL (UTF-8 Base64): " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(error.GetType().Name + ": " + error.Message + "\n" + error.StackTrace)));
            return 1;
        }
    }
    static int Run() {
        string root = @"C:\Users\Other 用户\AppData\Roaming";
        var paths = new HashSet<string>();
        for (int i = 1; i <= 10; i++) {
            string folder = Core.Folder(i, root);
            Check(paths.Add(folder), "Directories must be unique");
            string command = "\"" + @"C:\Program Files\WindowsApps\Claude_3_x64__demo\app\Claude.exe" + "\"" +
                (folder == null ? "" : " --user-data-dir=\"" + folder + "\"");
            Check(Core.NumberFromCommand(command, root) == i, "Incorrect profile number");
            Check(Core.NumberFromCommand(command + " --type=renderer", root) == 0, "Renderer counted as account");
        }
        Check(Core.Folder(2, root) == Path.Combine(root, "Claude-Second"), "Existing second account changed");
        Check(Core.NumberFromCommand("Claude.exe --user-data-dir=\"" + @"C:\Unknown" + "\"", root) == 0, "Foreign directory counted");
        Check(Core.IsDesktop(@"C:\Program Files\WindowsApps\Claude_3_x64__demo\app\Claude.exe"), "Store installation not recognized");
        Check(!Core.IsDesktop(@"C:\Users\someone\.local\bin\claude.exe"), "CLI mistaken for desktop");
        var running = new List<Instance> { new Instance { Number=1, ProcessId=111 }, new Instance { Number=2, ProcessId=222 } };
        Check(!Core.NeedsLaunch(1, running), "Clicking running profile would launch again");
        Check(!Core.NeedsLaunch(2, running), "Second profile would launch again");
        Check(Core.NeedsLaunch(3, running), "Missing third profile should launch");
        bool rejected=false;
        try { Core.Folder(11, root); } catch (ArgumentOutOfRangeException) { rejected=true; }
        Check(rejected, "Must reject profile 11");
        rejected=false;
        try { Core.NumberFromCommand(null, root); } catch (InvalidOperationException) { rejected=true; }
        Check(rejected, "Unreadable process must fail closed");
        string local = @"C:\Users\Other 用户\AppData\Local";
        for (int n = 1; n <= 10; n++) {
            foreach (string directory in ProfileDeletion.Candidates(n, root, local)) {
                Check(Core.NumberFromCommand("Claude.exe --user-data-dir=\"" + directory + "\"", root, local) == n, "Real/virtual directory mapped incorrectly");
                Check(Core.NumberFromCommand("Claude.exe --user-data-dir \"" + directory.Replace('\\', '/') + "/\"", root, local) == n, "Slash or space argument mapping failed");
            }
        }
        string[] installations = Core.InstallationCandidates(local, @"C:\Program Files", @"C:\Program Files (x86)");
        Check(installations.Contains(@"C:\Program Files\Claude\Claude.exe"), "Program Files installation missing");
        Check(installations.Contains(@"C:\Program Files (x86)\Claude\Claude.exe"), "Program Files x86 installation missing");
        foreach (string path in installations.Where(p => !p.EndsWith("claude-desktop.exe"))) Check(Core.IsDesktop(path), "Discovered install cannot be recognized on restart");
        Check(Core.IsDesktop(@"C:\Users\Customer\AppData\Local\AnthropicClaude\app-1.2.3\Claude.exe"), "Versioned installation not recognized");
        SelfTest.Run();
        DeletionTests.Run(Check);
        TestLaunches();
        UpdateTests.Run(Check);
        using (var watch = new ProcessWatch()) {
            var info = new System.Diagnostics.ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, "--exit-test-probe") { UseShellExecute=false, CreateNoWindow=true };
            using (var child = System.Diagnostics.Process.Start(info)) {
                watch.Refresh(new[] { new Instance { Number=10, ProcessId=child.Id } });
                Check(child.WaitForExit(5000), "Exit probe did not finish");
                bool recorded=false;
                for(int attempt=0;attempt<40;attempt++) {
                    try { recorded=File.ReadLines(Diagnostics.CurrentFile).Any(line=>line.Contains("account_process_exit") && line.Contains("pid="+child.Id+";") && line.Contains("exitCode=17")); } catch(IOException) { }
                    if(recorded) break;
                    System.Threading.Thread.Sleep(50);
                }
                Check(recorded, "Process exit code was not logged");
            }
        }
        // 构造原生界面以验证 XAML 和控件，测试不显示窗口、不启动 Claude。
        new System.Windows.Application();
        // 合成设置与临时设置路径：不读写用户真实的 settings.json。
        string settingsDir = Path.Combine(Path.GetTempPath(), "claude-settings-test-" + Guid.NewGuid().ToString("N"));
        string settingsFile = Path.Combine(settingsDir, "settings.json");
        var ui = new LauncherUI(new LauncherSettings(), settingsFile);
        // 01 不可删（Core.DeleteBlocker），删除确认框只用于 02～10。
        var dialog = ui.BuildDeleteDialog(3, new[] { @"C:\fixture\Claude-Profile-03" });
        var controls = Children(dialog).ToArray();
        var confirm = controls.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "ConfirmDelete");
        var cancel = controls.OfType<Button>().Single(b => b.IsCancel);
        Check(cancel.IsDefault && !confirm.IsDefault, "Enter must default to cancel");
        Check(controls.OfType<TextBox>().Single().IsReadOnly, "Deletion paths must be read-only");
        // 四个页面以合成状态渲染，不扫描真实进程。
        var state = new List<Instance> { new Instance { Number=1, ProcessId=111 }, new Instance { Number=2, ProcessId=222 } };
        for (int page = 0; page < 4; page++) {
            var pageRoot = ui.Snapshot(page, state);
            pageRoot.Measure(new Size(1080, 720)); pageRoot.Arrange(new Rect(0, 0, 1080, 720));
            Check(pageRoot.ActualWidth == 1080, "Page layout failed");
        }
        var buttons = Children(ui.Snapshot(0, state)).OfType<Button>().ToArray();
        Check(buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Profile1").Content as string == "打开", "Running account should offer Open");
        Check(buttons.Any(b => AutomationProperties.GetAutomationId(b) == "NewInstance"), "New instance button missing");
        // 托盘菜单账号项：与卡片同一份可见性和可用状态，只显示编号与运行状态。
        var trayItems = ui.TrayAccounts();
        Check(trayItems.Any(t => t.Item1 == 1 && t.Item2 == "账号 01 · 运行中" && t.Item3) && trayItems.Any(t => t.Item1 == 2 && t.Item2 == "账号 02 · 运行中"), "Tray account items wrong");
        Check(trayItems.All(t => System.Text.RegularExpressions.Regex.IsMatch(t.Item2, "^账号 [0-9]{2} · (运行中|未运行|检查中|启动中|删除未完成)$")), "Tray items must show only number and state");
        TestSettingsPage(ui, settingsFile, state);
        TestAccountsPage(ui, settingsFile, state);
        TestLogsPage(ui, settingsFile, state);
        TestUsageCards(ui, settingsFile, state);
        Directory.Delete(settingsDir, true);
        Check(ProxyCheck.LoopbackAddress("127.0.0.2").ToString() == "127.0.0.2", "Actual loopback retained");
        foreach (string bad in new[] { "127.evil", "127.1", "2130706433", "[127.0.0.1]", "127.00.0.1", "[::1", "8.8.8.8" }) Check(ProxyCheck.LoopbackAddress(bad) == null, "Strict loopback: " + bad);
        Check(ProxyCheck.IsLoopback("[::1]") && ProxyCheck.IsLoopback("localhost"), "IPv6 and localhost");
        Check(ProxyCheck.ProxyApplications(new[] { "v2rayN", "Hiddify", "Claude", "fake-clash" }).Length == 2, "Process names only");
        TestNewFeatures();
        SyncTests.Run();
        Console.WriteLine("PASS: " + checks + " assertions; WPF layout loaded without showing windows.");
        return 0;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string name, string existing, IntPtr security);
    static void TestNewFeatures() {
        var current = new LauncherSettings { UsageAlert = 70, LogLimit = 100, ShowTray = false };
        var imported = current.Import(System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"settings\":{\"usageAlert\":90,\"logLimit\":\"500\",\"showTray\":true,\"unknown\":true}}"));
        Check(imported.UsageAlert == 90 && imported.LogLimit == 100 && imported.ShowTray, "Import validates each value, preserves invalid");
        foreach (string bad in new[] { "{}", "{\"version\":2,\"settings\":{}}", "{\"version\":1,\"settings\":[]}" }) {
            bool rejected = false; try { current.Import(System.Text.Encoding.UTF8.GetBytes(bad)); } catch { rejected = true; } Check(rejected, "Invalid envelope rejected");
        }
        bool oversized = false; try { current.Import(new byte[65537]); } catch { oversized = true; } Check(oversized, "Import 64KB bound");
        Check(new LauncherSettings().UsageAlert == 80 && !new LauncherSettings().GlobalHotkeys, "Feature defaults");
        string root = Path.Combine(Path.GetTempPath(), "launcher-handoff-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            string preferences = Path.Combine(root, "settings.json");
            var alerts = new LauncherUI(new LauncherSettings(), preferences); alerts.Snapshot(0, new List<Instance>());
            var high = new UsageMetric { Percent = 80, Reset = "reset-A", Observed = DateTime.Now };
            Check(alerts.ObserveUsageAlert(2, high), "Threshold alerts once");
            Check(!alerts.ObserveUsageAlert(2, high), "Same reset deduped");
            var restarted = new LauncherUI(new LauncherSettings(), preferences); restarted.Snapshot(0, new List<Instance>());
            Check(!restarted.ObserveUsageAlert(2, high), "Alert state survives restart");
            Check(!restarted.ObserveUsageAlert(2, new UsageMetric { Percent = 79, Reset = "reset-A" }) && restarted.ObserveUsageAlert(2, high), "Below threshold rearms");
            var disabled = new LauncherUI(new LauncherSettings { UsageAlert = 0 }, preferences); disabled.Snapshot(0, new List<Instance>());
            Check(!disabled.ObserveUsageAlert(3, high), "Disabled alert");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var cached = (Dictionary<int, UsageRecord>)typeof(LauncherUI).GetField("savedUsage", flags).GetValue(alerts);
            cached[2] = new UsageRecord { Status = UsageInterface.Observed, Identity = "old", FiveHour = high };
            var oldSnapshot = alerts.Snapshot(0, new List<Instance>());
            typeof(LauncherUI).GetField("snapshotMode", flags).SetValue(alerts, false);
            typeof(LauncherUI).GetMethod("RenderUsage", flags).Invoke(alerts, null);
            var cardMap = (System.Collections.IDictionary)typeof(LauncherUI).GetField("cards", flags).GetValue(alerts);
            var source = (TextBlock)cardMap[2].GetType().GetField("Source").GetValue(cardMap[2]);
            Check(source.Text == "Claude 未运行 · 上次记录", "Stopped account keeps last usage");
            typeof(LauncherUI).GetField("snapshotMode", flags).SetValue(alerts, true);
            typeof(LauncherUI).GetMethod("RecordUse", flags).Invoke(alerts, new object[] { 2 });
            var recentReload = new LauncherUI(new LauncherSettings(), preferences); recentReload.Snapshot(0, new List<Instance>());
            Check((string)typeof(LauncherUI).GetMethod("Recent", flags).Invoke(recentReload, new object[] { 2 }) != "—", "Recent usage persists");
            string file = Path.Combine(root, "CLAUDE.md");
            Check(Handoff.Append(root) && File.ReadAllText(file) == Handoff.Rule, "Handoff creates exact rule");
            Check(!Handoff.Append(root) && File.ReadAllText(file) == Handoff.Rule, "Handoff deduplicates");
            File.WriteAllText(file, "existing"); Check(Handoff.Append(root) && File.ReadAllText(file).StartsWith("existing\n\n"), "Append preserves original");
            File.WriteAllBytes(file, new byte[] { 255, 254 }); bool rejected = false; try { Handoff.Append(root); } catch { rejected = true; } Check(rejected && File.ReadAllBytes(file).Length == 2, "Invalid UTF8 unchanged");
            File.WriteAllBytes(file, new byte[1048577]); rejected = false; try { Handoff.Append(root); } catch { rejected = true; } Check(rejected && new FileInfo(file).Length == 1048577, "Oversized file unchanged");
            File.Delete(file); string other = Path.Combine(root, "other"); File.WriteAllText(other, "original");
            Check(CreateHardLink(file, other, IntPtr.Zero), "Hardlink fixture"); rejected = false; try { Handoff.Append(root); } catch { rejected = true; } Check(rejected && File.ReadAllText(other) == "original", "Hardlink refused"); File.Delete(file);
            Directory.CreateDirectory(file); rejected = false; try { Handoff.Append(root); } catch { rejected = true; } Check(rejected, "Directory refused"); Directory.Delete(file);
            Check(Handoff.Age(root) == null && Handoff.ContinuePrompt(root).Contains(root), "Handoff metadata and prompt");
            File.WriteAllText(Path.Combine(root, "HANDOFF.md"), "fixture"); Check(Handoff.Age(root).StartsWith("交接 "), "Handoff age");
            var cancelled = new System.Threading.CancellationToken(true); rejected = false; try { LauncherUI.DirectoryBytes(root, cancelled); } catch (OperationCanceledException) { rejected = true; } Check(rejected, "Directory size cancellation");
            Check(LauncherUI.DirectoryBytes(root, System.Threading.CancellationToken.None) > 0, "Directory size");
        } finally { Directory.Delete(root, true); }
    }

    // SPEC 系统设置 [Windows]：六分类、更改即时保存生效、恢复默认立即保存、更新入口受开关约束。
    static void TestSettingsPage(LauncherUI ui, string settingsFile, List<Instance> state) {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var all = Children(ui.Snapshot(3, state)).ToArray();
        Func<string, CheckBox> toggle = name => all.OfType<CheckBox>().Single(b => AutomationProperties.GetName(b) == name);
        Func<string, ComboBox> choice = name => all.OfType<ComboBox>().Single(b => AutomationProperties.GetName(b) == name);
        Func<string, Button> button = id => all.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id || AutomationProperties.GetName(b) == id);
        Action<System.Windows.Controls.Primitives.ButtonBase> click = b => typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", flags).Invoke(b, null);
        Func<System.Threading.Tasks.Task> check = () => (System.Threading.Tasks.Task)typeof(LauncherUI).GetMethod("CheckUpdate", flags).Invoke(ui, new object[] { false });

        foreach (string tab in new[] { "常规", "启动行为", "外观", "数据与存储", "通知", "关于" }) Check(all.OfType<Button>().Count(b => AutomationProperties.GetName(b) == tab) == 1, "Settings tab missing: " + tab);
        Check(!all.OfType<FrameworkElement>().Any(e => e is TextBlock && new[] { "默认浏览器", "启动间隔", "默认打开方式" }.Contains(((TextBlock)e).Text)), "Fake settings must be removed");
        Check(!all.OfType<Button>().Any(b => new[] { "SaveSettings", "UndoSettings" }.Contains(AutomationProperties.GetAutomationId(b))), "Save and undo buttons must be removed");
        Check(toggle("显示水墨插画").IsChecked == true && toggle("允许检查 GitHub 更新").IsChecked == false && !File.Exists(settingsFile), "Settings must start from saved defaults");
        click(toggle("显示水墨插画"));
        Check(toggle("显示水墨插画").IsChecked == false && !LauncherSettings.Load(settingsFile).ShowArtwork, "Toggle must save immediately");
        click(toggle("显示水墨插画"));
        Check(LauncherSettings.Load(settingsFile).ShowArtwork, "Toggle back must save immediately");
        Check(choice("未恢复时的启动页面").IsEnabled, "Start page selectable when not restoring");
        click(toggle("恢复启动器上次页面"));
        Check(!choice("未恢复时的启动页面").IsEnabled && LauncherSettings.Load(settingsFile).RememberPage, "Start page disabled while restoring last page");
        click(toggle("恢复启动器上次页面"));

        var addLog = typeof(LauncherUI).GetMethod("AddLog", flags);
        for (int i = 0; i < 600; i++) addLog.Invoke(ui, new object[] { "合成日志 " + i });
        var logs = (System.Collections.IList)typeof(LauncherUI).GetField("logs", flags).GetValue(ui);
        Check(logs.Count == 500, "Default log limit must be 500");
        choice("启动器日志保留上限").SelectedIndex = 0;
        click(toggle("显示水墨插画")); click(toggle("显示窗口内提示"));
        var saved = LauncherSettings.Load(settingsFile);
        Check(saved.LogLimit == 100 && !saved.ShowArtwork && !saved.LocalNotifications && saved.LoadError == null, "Each change must be written");
        Check(logs.Count == 100, "Saved log limit must trim memory logs");
        var artwork = (List<UIElement>)typeof(LauncherUI).GetField("artwork", flags).GetValue(ui);
        Check(artwork.Count > 0 && artwork.All(e => e.Visibility == Visibility.Collapsed), "Artwork must follow the saved setting");
        Check(!ui.Notify("测试"), "Notifications off must not show the toast");
        ui.ResetSettings();
        Check(LauncherSettings.Load(settingsFile).SameAs(new LauncherSettings()) && artwork.All(e => e.Visibility == Visibility.Visible) && ui.Notify("测试"), "Reset must save and apply defaults");

        // 更新入口：未开启时不发请求；开启即保存。
        int before = Updates.Requests;
        check().Wait();
        Check(Updates.Requests == before, "Update check must not run while disabled");
        click(toggle("允许检查 GitHub 更新"));
        Check(LauncherSettings.Load(settingsFile).AllowUpdates, "Updates switch must save immediately");
        click(toggle("允许检查 GitHub 更新"));
        Check(!LauncherSettings.Load(settingsFile).AllowUpdates, "Updates must be off again");
        string diagnostics = ui.DiagnosticsText();
        Check(diagnostics.Contains(Updates.DisplayVersion) && diagnostics.Contains("GitHub updates disabled") && !diagnostics.Contains(":\\"), "Diagnostics must contain only version, OS and network switch");
    }
    // SPEC 账号与日志交互：搜索、状态筛选、别名、可见选择（01、02 为合成运行中进程；其余是否显示取决于本机已有目录，不作断言）。
    static void TestAccountsPage(LauncherUI ui, string settingsFile, List<Instance> state) {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var all = Children(ui.Snapshot(1, state)).ToArray();
        var search = all.OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "搜索账号");
        var status = all.OfType<ComboBox>().Single(b => AutomationProperties.GetName(b) == "账号状态筛选");
        var selectAll = all.OfType<CheckBox>().Single(b => AutomationProperties.GetName(b) == "全选当前结果");
        var copy = all.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "CopySelectedPaths");
        Action<System.Windows.Controls.Primitives.ButtonBase> click = b => typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", flags).Invoke(b, null);
        Func<int, CheckBox> rowBox = n => all.OfType<CheckBox>().Single(b => AutomationProperties.GetName(b) == "选择账号 " + n.ToString("00"));
        Func<int, bool> shown = n => ((FrameworkElement)((FrameworkElement)rowBox(n).Parent).Parent).Visibility == Visibility.Visible;
        var selected = (HashSet<int>)typeof(LauncherUI).GetField("selectedAccounts", flags).GetValue(ui);

        Check(shown(1) && shown(2) && !copy.IsEnabled && selectAll.IsChecked == false, "Running accounts listed, nothing selected");
        search.Text = "账号 02";
        Check(!shown(1) && shown(2), "Search by number must filter rows");
        search.Text = "";
        status.SelectedIndex = Array.IndexOf(new[] { "全部状态", "运行中", "未运行", "处理中", "异常" }, "未运行");
        Check(!shown(1) && !shown(2), "Status filter must hide running accounts");
        status.SelectedIndex = 1;
        Check(shown(1) && shown(2), "Running filter must show running accounts");
        click(selectAll);
        Check(selected.SetEquals(new[] { 1, 2 }) && selectAll.IsChecked == true && copy.IsEnabled, "Select all must select current results only");
        Check(ui.SelectedPathsText().Split(new[] { "\r\n" }, StringSplitOptions.None).Length == 2, "Copy must list visible selected folders");
        search.Text = "账号 02";
        Check(selected.SetEquals(new[] { 2 }) && selectAll.IsChecked == true, "Hidden selections must be cleared");
        search.Text = ""; click(rowBox(1));
        Check(selected.SetEquals(new[] { 1, 2 }) && selectAll.IsChecked == true, "Row checkbox toggles selection");
        click(rowBox(2));
        Check(selected.SetEquals(new[] { 1 }) && selectAll.IsChecked == null, "Partial selection shows mixed state");
        click(selectAll); click(selectAll);
        Check(selected.Count == 0 && selectAll.IsChecked == false && !copy.IsEnabled, "Select all toggles off");
        status.SelectedIndex = 0;

        Check(ui.SetAlias(2, "  工作  ") == null, "Valid alias rejected");
        var names = AccountNames.Load(Path.Combine(Path.GetDirectoryName(settingsFile), "account-names.json"));
        Check(names.Count == 1 && names[2] == "工作", "Alias must be saved to the launcher's own file");
        Check(all.OfType<TextBlock>().Count(t => t.Text == "工作 · 02") == 2, "Card and table must both show alias with number");
        search.Text = "工作";
        Check(shown(2) && !shown(1), "Search must match alias");
        search.Text = "";
        Check(ui.SetAlias(2, "a\nb") != null && ui.SetAlias(2, new string('长', 25)) != null && AccountNames.Load(Path.Combine(Path.GetDirectoryName(settingsFile), "account-names.json"))[2] == "工作", "Invalid alias must be rejected without changes");
        Check(ui.SetAlias(2, "   ") == null && all.OfType<TextBlock>().Count(t => t.Text == "账号 02") == 2, "Empty alias restores default name");
        Check(!new LauncherSettings().ExportJson().Contains("工作"), "Aliases must not be exported with settings");

        // 默认用户名（合成 .claude.json，不读真实文件）：无别名时显示“用户名 · NN”，别名优先，文件变化后重读，缺失回落“账号 NN”。
        string nameFile = Path.Combine(Path.GetDirectoryName(settingsFile), "code-02.claude.json");
        File.WriteAllText(nameFile, "{\"oauthAccount\":{\"displayName\":\"Eric\",\"emailAddress\":\"x@example.invalid\"}}");
        ui.CodeNameFiles = n => n == 2 ? new[] { nameFile } : new string[0];
        var renderAccounts = typeof(LauncherUI).GetMethod("Render", flags);
        renderAccounts.Invoke(ui, null);
        Check(all.OfType<TextBlock>().Count(t => t.Text == "Eric · 02") == 2 && all.OfType<TextBlock>().Count(t => t.Text == "账号 01") == 2, "Default name from local Claude Code profile");
        search.Text = "eric";
        Check(shown(2) && !shown(1), "Search must match the default user name");
        search.Text = "";
        Check(ui.SetAlias(2, "工作") == null && all.OfType<TextBlock>().Count(t => t.Text == "工作 · 02") == 2, "Alias overrides the default name");
        Check(ui.SetAlias(2, "") == null && all.OfType<TextBlock>().Count(t => t.Text == "Eric · 02") == 2, "Clearing alias falls back to the default name");
        File.WriteAllText(nameFile, "{\"oauthAccount\":{\"displayName\":\"Eric Wang\"}}");
        renderAccounts.Invoke(ui, null);
        Check(all.OfType<TextBlock>().Count(t => t.Text == "Eric Wang · 02") == 2, "Changed file must be re-read");
        File.Delete(nameFile);
        renderAccounts.Invoke(ui, null);
        Check(all.OfType<TextBlock>().Count(t => t.Text == "账号 02") == 2 && !ui.DiagnosticsText().Contains("Eric") && !new LauncherSettings().ExportJson().Contains("Eric"), "Missing file falls back; name never exported or in diagnostics");
        ui.CodeNameFiles = n => new string[0];

        // 实例卡片只显示名称与状态；目录与 Code 状态在悬停提示中。
        var cardTexts = Children(ui.Snapshot(0, state)).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(!cardTexts.Any(t => t.StartsWith("Code ") || t == "默认目录" || t == "Second"), "Cards must not show folder or Code chips");
        var cards = (System.Collections.IDictionary)typeof(LauncherUI).GetField("cards", flags).GetValue(ui);
        string tip = ((Border)cards[2].GetType().GetField("Root").GetValue(cards[2])).ToolTip as string;
        Check(tip != null && tip.Contains("目录：") && tip.Contains("Code "), "Card tooltip must carry folder and Code status");
    }
    // SPEC 本地界面用量 [Windows]：卡片显示合成观察记录；关闭显示后清空并删除记录文件。不读取真实 Claude。
    static void TestUsageCards(LauncherUI ui, string settingsFile, List<Instance> state) {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var page = ui.Snapshot(0, state);
        Func<IEnumerable<string>> texts = () => Children(page).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(!texts().Contains("未接入") && texts().Count(t => t == "尚未观察到") >= 4, "Usage rows must show not-yet-observed instead of placeholder");
        DateTime at = DateTime.Now.AddMinutes(-1);
        ui.SetUsage(2, new UsageRecord { Status = UsageInterface.Observed, Weekly = new UsageMetric { Percent = 11, Reset = "周三19:00 重置", Observed = at } });
        Check(texts().Contains("11%") && texts().Contains(at.ToString("HH:mm:ss")), "Weekly observation must render on the card");
        var cards = (System.Collections.IDictionary)typeof(LauncherUI).GetField("cards", flags).GetValue(ui);
        var values = (TextBlock[])cards[2].GetType().GetField("Values").GetValue(cards[2]);
        Check(values[0].Text == "—" && (values[1].ToolTip as string).Contains("周三19:00 重置") && (values[1].ToolTip as string).Contains("组织"), "Tooltip must carry reset text and the organization limit");
        string usageFile = Path.Combine(Path.GetDirectoryName(settingsFile), "usage-observations.json");
        File.WriteAllText(usageFile, "{}");
        var all = Children(ui.Snapshot(3, state)).ToArray();
        var toggle = all.OfType<CheckBox>().Single(b => AutomationProperties.GetName(b) == "显示本地界面用量");
        Check(toggle.IsChecked == true, "Usage display defaults on");
        typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", flags).Invoke(toggle, null);
        typeof(LauncherUI).GetMethod("SaveSettings", flags).Invoke(ui, null);
        page = ui.Snapshot(0, state);
        Check(!File.Exists(usageFile) && !texts().Contains("11%") && texts().Contains("本地用量显示已关闭"), "Turning usage off must clear values and delete observations");
    }
    static void TestLogsPage(LauncherUI ui, string settingsFile, List<Instance> state) {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var logs = (List<LogEntry>)typeof(LauncherUI).GetField("logs", flags).GetValue(ui);
        var addLog = typeof(LauncherUI).GetMethod("AddLog", flags);
        var render = typeof(LauncherUI).GetMethod("RenderLogs", flags);
        logs.Clear();
        addLog.Invoke(ui, new object[] { "账号 02 启动失败" });
        for (int i = 0; i < 15; i++) addLog.Invoke(ui, new object[] { "普通信息 " + i });
        logs[logs.Count - 2].At = DateTime.Now.AddMinutes(-10); // “普通信息 0”（系统）
        var all = Children(ui.Snapshot(2, state)).ToArray();
        Func<string, ComboBox> choice = name => all.OfType<ComboBox>().Single(b => AutomationProperties.GetName(b) == name);
        var search = all.OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "搜索日志");
        var prev = all.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "LogPrevious");
        var next = all.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "LogNext");
        var rows = (Canvas)typeof(LauncherUI).GetField("logRows", flags).GetValue(ui);
        Action<System.Windows.Controls.Primitives.ButtonBase> click = b => typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", flags).Invoke(b, null);
        Func<int> visibleEntries = () => rows.Children.OfType<TextBox>().Count();

        render.Invoke(ui, null);
        Check(visibleEntries() == 10 && !prev.IsEnabled && next.IsEnabled, "First page shows 10 entries");
        click(next);
        Check(visibleEntries() == 6 && prev.IsEnabled && !next.IsEnabled, "Second page shows the remainder");
        search.Text = "失败";
        Check(visibleEntries() == 1 && !prev.IsEnabled && !next.IsEnabled, "Search resets to first page and filters");
        search.Text = "";
        choice("日志级别筛选").SelectedIndex = 3;
        Check(visibleEntries() == 1, "Level filter");
        choice("日志级别筛选").SelectedIndex = 0; choice("日志实例筛选").SelectedIndex = 3;
        Check(visibleEntries() == 1, "Instance filter (Claude 2)");
        choice("日志实例筛选").SelectedIndex = 1;
        Check(choice("日志时间范围").Items.Cast<string>().SequenceEqual(new[] { "最近 7 天", "最近 24 小时", "最近 1 小时", "最近 5 分钟", "本次运行", "全部时间" }) && choice("日志时间范围").SelectedIndex == 0, "Time filter options and 7-day default");
        choice("日志时间范围").SelectedIndex = 3;
        Check(LauncherUI.FilterLogs(logs, "", "系统", null, DateTime.Now.AddMinutes(-5)).Count == 14 && visibleEntries() == 10, "Time filter excludes entries older than 5 minutes");
        search.Text = "不存在的内容";
        Check(visibleEntries() == 0 && rows.Children.OfType<TextBlock>().Any(t => t.Text.StartsWith("没有匹配的日志")), "Empty filtered result explains itself");
        search.Text = ""; choice("日志实例筛选").SelectedIndex = 0; choice("日志时间范围").SelectedIndex = 5;
        var pager = (StackPanel)typeof(LauncherUI).GetField("logPager", flags).GetValue(ui);
        var pageButtons = pager.Children.OfType<Button>().Where(b => AutomationProperties.GetName(b).StartsWith("第 ")).ToList();
        Check(pageButtons.Count == 2 && pager.Children[0] == prev && pager.Children[pager.Children.Count - 1] == next, "Numbered pager: previous, 1, 2, next");
        click(pageButtons[1]);
        Check(visibleEntries() == 6 && prev.IsEnabled, "Page number navigates");
        var summary = (TextBlock)typeof(LauncherUI).GetField("logSummary", flags).GetValue(ui);
        Check(summary.Text == "共 16 条日志 · 每页显示 10 条", "Summary text: " + summary.Text);
        choice("日志时间范围").SelectedIndex = 0;
        string exported = LauncherUI.LogText(LauncherUI.FilterLogs(logs, "信息", null, null, null));
        string[] lines = exported.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == 15 && lines[0].EndsWith("普通信息 0") && lines[14].EndsWith("普通信息 14"), "Export writes all filtered entries oldest first");
        Check(rows.Children.OfType<TextBox>().All(t => t.IsReadOnly && t.ToolTip is string), "Log text is read-only, selectable and has a full-text tooltip");

        // 持久化：写入启动器自己的日志文件（临时目录），重启后恢复；“本次运行”只含新进程的记录。
        string logFile = Path.Combine(Path.GetDirectoryName(settingsFile), "launcher-log.json");
        var saved = LogStore.Load(logFile);
        Check(saved.Count == logs.Count && saved[0].Text == logs[0].Text, "Logs must be written to launcher-log.json");
        System.Threading.Thread.Sleep(20);
        var restarted = new LauncherUI(new LauncherSettings(), settingsFile);
        var restored = (List<LogEntry>)typeof(LauncherUI).GetField("logs", flags).GetValue(restarted);
        Check(restored.Count == saved.Count + 1 && restored.Any(e => e.Text == "账号 02 启动失败"), "Logs must survive a restart");
        ((ComboBox)typeof(LauncherUI).GetField("logTime", flags).GetValue(restarted)).SelectedIndex = 4;
        var thisRun = (List<LogEntry>)typeof(LauncherUI).GetMethod("FilteredLogs", flags).Invoke(restarted, null);
        Check(thisRun.Count == 1 && thisRun[0].Text == "启动器已完成初始化", "“本次运行” must only show entries since this launch");
    }
    static void TestLaunches() {
        string fixture = Path.Combine(Path.GetTempPath(), "claude-launch-test-" + Guid.NewGuid().ToString("N"), "客户 测试 & O'Neil");
        Directory.CreateDirectory(fixture);
        string executable = Path.Combine(fixture, "启动探针.exe");
        File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, executable);
        string roaming = Path.Combine(fixture, "Roaming");
        try {
            var running = new List<Instance>();
            for (int n = 1; n <= 10; n++) {
                Check(Core.NeedsLaunch(n, running), "New slot blocked by an existing account");
                var info = Core.PrepareLaunch(n, executable, roaming);
                info.Arguments += " --launcher-test-probe";
                info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true;
                using (var process = System.Diagnostics.Process.Start(info)) {
                    string[] output = process.StandardOutput.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(x))).ToArray();
                    Check(process.WaitForExit(10000) && process.ExitCode == 0, "Launch probe failed");
                    string argument = output.Length < 2 ? "" : output[1];
                    Check(output.Length == (n == 1 ? 1 : 2) && argument == (n == 1 ? "" : "--user-data-dir=" + Core.Folder(n, roaming)), "Account path was split or altered during process launch");
                    Check(output[0] == "env:" + (n == 1 ? "" : Core.CodeConfig(Core.Folder(n, roaming))), "CLAUDE_CONFIG_DIR not passed (or leaked into 01)");
                }
                if (n > 1) {
                    string folder = Core.Folder(n, roaming), marker = Path.Combine(folder, "existing-data.txt");
                    Check(Directory.Exists(folder), "New account directory was not created");
                    File.WriteAllText(marker, "account-" + n);
                    Core.PrepareLaunch(n, executable, roaming);
                    Check(File.ReadAllText(marker) == "account-" + n, "Reopening destroyed account data");
                    for (int prior = 2; prior < n; prior++) Check(File.ReadAllText(Path.Combine(Core.Folder(prior, roaming), "existing-data.txt")) == "account-" + prior, "Adding account changed an existing account");
                }
                running.Add(new Instance { Number=n, ProcessId=1000+n });
                Check(!Core.NeedsLaunch(n, running), "Repeated click should reuse running account");
            }
        } finally {
            string parent = Path.GetFullPath(Path.GetDirectoryName(fixture));
            if (!parent.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(parent).StartsWith("claude-launch-test-")) throw new IOException("Invalid launch fixture cleanup root");
            RecycleBin.Move(parent);
        }
    }
}
