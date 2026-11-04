using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeLauncher;
// 以合成进程状态离屏渲染四个页面与更新进度窗口；不扫描或启动 Claude，不打开桌面窗口。
public static class UiPreview {
    static void Save(FrameworkElement root, Size size, string path) {
        Appearance.TranslateTree(root); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
    static System.Collections.Generic.IEnumerable<DependencyObject> Tree(DependencyObject root) {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root)) { var node = child as DependencyObject; if (node != null) foreach (var item in Tree(node)) yield return item; }
    }
    static void SaveJpeg(FrameworkElement root, Size size, string path) {
        Appearance.TranslateTree(root); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var flat = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 }; encoder.Frames.Add(BitmapFrame.Create(flat));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
    // 文档配图：只用演示数据。项目路径在临时目录，显示前换成演示路径，不把本机用户名画进图里。
    static void GuideImages(string output) {
        string[] languages = { "zh", "en" };
        for (int language = 0; language < 2; language++) {
            string temp = Path.Combine(Path.GetTempPath(), "claude-guide-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            string[] names = { "demo-app", "demo-site", "demo-notes" }; var projects = new List<LauncherUI.LocalProject>(); var real = new Dictionary<string, string>();
            for (int i = 0; i < names.Length; i++) { string dir = Path.Combine(temp, names[i]); Directory.CreateDirectory(dir); projects.Add(new LauncherUI.LocalProject { Path = dir, Name = names[i], Group = i == 0 ? "Demo" : "", LastAccount = i + 1 }); real[dir] = @"D:\Projects\" + names[i]; }
            File.WriteAllText(Path.Combine(temp, "settings.json.projects"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(projects));
            var ui = new LauncherUI(new LauncherSettings { Language = language }, Path.Combine(temp, "settings.json"));
            var state = new List<Instance> { new Instance { Number = 1, ProcessId = 111 }, new Instance { Number = 2, ProcessId = 222 } };
            DateTime local = DateTime.Now, now = DateTime.UtcNow;
            ui.SetUsage(1, new UsageRecord { Status = UsageInterface.Observed, FiveHour = new UsageMetric { Percent = 62, Reset = "3:20", Observed = local.AddMinutes(-12) }, Weekly = new UsageMetric { Percent = 34, Reset = "Wed 19:00", Observed = local.AddMinutes(-12) } });
            ui.SetUsage(2, new UsageRecord { Status = UsageInterface.Observed, FiveHour = new UsageMetric { Percent = 18, Reset = "5:10", Observed = local.AddMinutes(-40) }, Weekly = new UsageMetric { Percent = 21, Reset = "Fri 09:00", Observed = local.AddMinutes(-40) } });
            var size = new Size(1080, 720);
            SaveJpeg(ui.Snapshot(0, state), size, Path.Combine(output, "guide-instances." + languages[language] + ".jpg"));
            var page = ui.Snapshot(4, state);
            foreach (var text in Tree(page).OfType<System.Windows.Controls.TextBlock>()) { string tip = text.ToolTip as string; if (tip != null && real.ContainsKey(tip) && text.Text.Contains("\\")) { text.Text = real[tip]; text.ToolTip = real[tip]; } }
            foreach (var text in Tree(page).OfType<System.Windows.Controls.TextBlock>()) if (text.Text.IndexOf(Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidOperationException("guide picture would show the local user name");
            SaveJpeg(page, size, Path.Combine(output, "guide-projects." + languages[language] + ".jpg"));
            var points = new List<TrendPoint>();
            for (int i = 40; i >= 0; i--) { points.Add(new TrendPoint { Account = 1, At = now.AddHours(-i * 3), Five = (i * 7) % 60 + 5, Week = 60 - i }); points.Add(new TrendPoint { Account = 2, At = now.AddHours(-i * 3), Five = (i * 11) % 40 + 5, Week = 40 - i / 2 }); }
            var accounts = ui.Snapshot(1, state); ui.SetTrend(points);
            Appearance.TranslateTree(accounts); accounts.Measure(size); accounts.Arrange(new Rect(size)); accounts.UpdateLayout();
            var scroll = (System.Windows.Controls.ScrollViewer)typeof(LauncherUI).GetField("accountsScroll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(ui);
            scroll.ScrollToVerticalOffset(360); scroll.UpdateLayout();
            SaveJpeg(accounts, size, Path.Combine(output, "guide-trend." + languages[language] + ".jpg"));
            Directory.Delete(temp, true);
        }
        Console.WriteLine("PASS: guide pictures rendered from demo data.");
    }
    [STAThread]
    public static void Main(string[] args) {
        Diagnostics.Disabled=true;
        new Application();
        if (args.Length == 2 && args[0] == "--guide") { GuideImages(args[1]); return; }
        // 合成默认设置与临时目录（预览不保存设置；日志文件写入临时目录，结束时删除）。
        string temp = Path.Combine(Path.GetTempPath(), "claude-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string example = Path.Combine(temp, "示例项目"); Directory.CreateDirectory(example); File.WriteAllText(Path.Combine(example, "HANDOFF.md"), "合成预览");
        File.WriteAllText(Path.Combine(temp, "settings.json.projects"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new[] { new LauncherUI.LocalProject { Path = example, LastAccount = 1 } }));
        var ui = new LauncherUI(new LauncherSettings { Theme=args.Length>1?Int32.Parse(args[1]):1, Language=args.Length>2?Int32.Parse(args[2]):0 }, Path.Combine(temp, "settings.json"));
        var state = new List<Instance> { new Instance { Number = 1, ProcessId = 111 } };
        ui.Snapshot(0, state);
        // 合成用量观察：五小时与周各一项，验证卡片右侧布局。
        ui.SetUsage(1, new UsageRecord { Status = UsageInterface.Observed, FiveHour = new UsageMetric { Percent = 71, Reset = "3:20 重置", Observed = DateTime.Now.AddMinutes(-40) }, Weekly = new UsageMetric { Percent = 11, Reset = "周三19:00 重置", Observed = DateTime.Now.AddMinutes(-2) } });
        string[] names = { "instances", "accounts", "logs" };
        Directory.CreateDirectory(args[0]);
        for (int page = 0; page < 3; page++) Save(ui.Snapshot(page, state), new Size(1080, 720), Path.Combine(args[0], names[page] + ".png"));
        // 合成趋势：账号页滚到趋势区。
        DateTime now = DateTime.UtcNow; var points = new List<TrendPoint>();
        for (int i = 40; i >= 0; i--) points.Add(new TrendPoint { Account = 1, At = now.AddHours(-i * 3), Five = (i * 7) % 60 + 5, Week = 60 - i });
        var accountsRoot = ui.Snapshot(1, state);
        ui.SetTrend(points); Save(accountsRoot, new Size(1080, 720), Path.Combine(args[0], "accounts-top.png"));
        var scroll = (System.Windows.Controls.ScrollViewer)typeof(LauncherUI).GetField("accountsScroll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(ui);
        scroll.ScrollToVerticalOffset(380); scroll.UpdateLayout(); Save(accountsRoot, new Size(1080, 720), Path.Combine(args[0], "accounts-trend.png"));
        ui.SetTrend(new TrendPoint[0]);
        Save(ui.Snapshot(4, state), new Size(1080, 720), Path.Combine(args[0], "projects.png"));
        // 系统设置六个分类各一张。
        var section = typeof(LauncherUI).GetMethod("ShowSettingsSection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        for (int i = 0; i < 6; i++) { section.Invoke(ui, new object[] { i }); Save(ui.Snapshot(3, state), new Size(1080, 720), Path.Combine(args[0], "settings-" + (i + 1) + ".png")); }
        // 新手教程：全部步骤、选择窗和文档。
        var tourRoot = ui.Snapshot(0, state); ui.StartTutorial();
        for (int i = 0; i < 8; i++) { Save(tourRoot, new Size(1080, 720), Path.Combine(args[0], "tutorial-" + (i + 1) + ".png")); ui.TutorialKey(System.Windows.Input.Key.Return); }
        ui.ShowTutorialPicker(); Save(tourRoot, new Size(1080, 720), Path.Combine(args[0], "tutorial-picker.png")); ui.CloseTutorial();
        var guide = ui.BuildTutorialDocument(); var guideView = (FrameworkElement)guide.Content; guide.Content = null;
        Save(new System.Windows.Controls.Border { Background = guide.Background, Child = guideView }, new Size(760, 720), Path.Combine(args[0], "tutorial-document.png"));
        // 同步会话：选择窗与预览。
        Func<int, long, SyncVersion> version = (n, stamp) => new SyncVersion { Id = "00000000-0000-4000-8000-00000000000" + n, Stamp = stamp };
        var demo = new List<SyncSessionRow> {
            new SyncSessionRow { Path = "a", Title = "修复登录页样式", Account = 1, ProjectPath = @"D:\Projects\demo-app", ProjectName = "demo-app", Modified = DateTime.Now, Stamp = 300, Linked = true, Versions = new Dictionary<int, SyncVersion> { { 1, version(1, 300) }, { 2, version(2, 200) } } },
            new SyncSessionRow { Path = "b", Title = "整理发布说明", Account = 1, ProjectPath = @"D:\Projects\demo-app", ProjectName = "demo-app", Modified = DateTime.Now.AddHours(-3), Stamp = 250, Versions = new Dictionary<int, SyncVersion> { { 1, version(3, 250) } } },
            new SyncSessionRow { Path = "c", Title = "写入门文档", Account = 1, ProjectPath = @"D:\Projects\demo-app", ProjectName = "demo-app", Modified = DateTime.Now.AddDays(-1), Stamp = 100, Linked = true, Versions = new Dictionary<int, SyncVersion> { { 1, version(4, 100) }, { 2, version(5, 100) } } },
            new SyncSessionRow { Path = "d", Title = "接口草稿", Account = 3, ProjectPath = @"D:\Projects\demo-site", ProjectName = "demo-site", Modified = DateTime.Now.AddDays(-2), Stamp = 90, Versions = new Dictionary<int, SyncVersion> { { 3, version(6, 90) } } } };
        var picker = ui.BuildSessionPicker(demo, new[] { 1, 2, 3 }, new[] { 1 }); picker.Expanders[0].IsExpanded = true;
        var pickerView = (FrameworkElement)picker.Dialog.Content; picker.Dialog.Content = null;
        Save(new System.Windows.Controls.Border { Background = picker.Dialog.Background, Child = pickerView, Resources = picker.Dialog.Resources, Width = 700, Height = 640 }, new Size(700, 640), Path.Combine(args[0], "session-picker.png"));
        var review = ui.BuildSessionPreview(new List<CleanedSession> { new CleanedSession { Title = "修复登录页样式（同步 10/31 12:05）", Report = "保留 4 条消息，2 条工具说明。\n丢弃：3 个工具结果、1 个思考块、0 张图片、0 行子代理记录、2 行非主链记录。\n替换：1 处邮箱／密钥／令牌，1 个项目外路径。", Preview = "【用户】\n[这是从其他账号同步的清理副本…]\n\n登录页按钮错位，帮我看看。\n\n【助手】\n[历史工具请求：Edit，src/login.css]\n已修复。\n", Jsonl = new byte[1200], From = 1, Replaces = true }, new CleanedSession { Title = "整理发布说明（同步 10/31 12:05）", Report = "保留 2 条消息，0 条工具说明。", Preview = "【用户】\n列一下这次发布的改动。\n\n【助手】\n共三项。\n", Jsonl = new byte[600], From = 1 } }, new List<string>(), 2, () => { });
        var reviewView = (FrameworkElement)review.Content; review.Content = null;
        Save(new System.Windows.Controls.Border { Background = review.Background, Child = reviewView, Resources = review.Resources }, new Size(700, 600), Path.Combine(args[0], "session-preview.png"));
        var support = ui.BuildSupportDialog(new SupportResult { Summary = "发现 1 个问题、1 个注意事项", Text = "Claude 多开启动器 · 问题诊断\n版本 1.8.8；Windows 10.0.26200\n\n== 结论 ==\n  [问题] 账号 01：记录目录 claude-code-sessions\\… 被当作不安全，整层跳过：名字不是 GUID 的文件夹「backup」。这一部分的会话不会出现在同步窗口里。\n  [注意] P2「Beta」 在同步窗口里不显示：4 条会话在来源账号里是“已归档”。\n\n== 同步窗口 ==\n  会显示的项目：P1「Alpha」（9）\n  不会显示的项目：P2「Beta」、P3「Gamma」\n" }, @"C:\reports\diagnose.txt");
        var supportView = (FrameworkElement)support.Content; support.Content = null;
        Save(new System.Windows.Controls.Border { Background = support.Background, Child = supportView, Resources = support.Resources, Width = 720 }, new Size(720, 640), Path.Combine(args[0], "support-report.png"));
        var offer = ui.BuildAsk("账号 02 已准备好", LauncherUI.InitialSyncOffer, new[] { "选择并同步…", "以后再说" }, 1, 0, index => { }); var offerView = (FrameworkElement)offer.Content; offer.Content = null;
        Save(new System.Windows.Controls.Border { Background = offer.Background, Child = offerView, Resources = offer.Resources, Width = 520 }, new Size(520, 360), Path.Combine(args[0], "initial-sync.png"));
        // 更新进度窗口：下载中与校验中两种状态。
        var progress = ui.BuildUpdateProgress("1.7.2");
        var content = (FrameworkElement)progress.Window.Content;
        progress.Window.Content = null;
        var background = new System.Windows.Controls.Border { Background = progress.Window.Background, Child = content, Resources = progress.Window.Resources };
        progress.Source.Text = "来源：GitHub";
        progress.Status.Text = "正在从GitHub下载 ClaudeLauncherSetup-1.7.2.exe…";
        progress.Bar.Value = 45; progress.Detail.Text = "1.3 MB / 2.8 MB · 45%";
        Save(background, new Size(460, 300), Path.Combine(args[0], "update-download.png"));
        typeof(LauncherUI).GetMethod("SetUpdateStep", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { progress, 2 });
        progress.Cancel.IsEnabled = false; progress.Bar.Value = 100;
        progress.Status.Text = "校验通过。启动器即将关闭，由安装程序卸载旧版并安装新版，完成后会自动重新打开。";
        progress.Detail.Text = "请稍候，不要手动打开启动器。";
        Save(background, new Size(460, 300), Path.Combine(args[0], "update-install.png"));
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Console.WriteLine("PASS: four pages (including projects), six settings sections and two update states rendered offscreen; no desktop window opened.");
    }
}
