using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeLauncher;
// 新手教程：合成状态 + 临时设置路径，不打开窗口、不启动账号。
internal static class TutorialTests {
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception("tutorial: " + name); }
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(LauncherUI ui, string name) { return (T)typeof(LauncherUI).GetField(name, Flags).GetValue(ui); }
    static int StepCount(LauncherUI ui) { return ((System.Collections.ICollection)typeof(LauncherUI).GetField("tutorialSteps", Flags).GetValue(ui)).Count; }
    static IEnumerable<DependencyObject> Tree(DependencyObject root) {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root)) { var node = child as DependencyObject; if (node != null) foreach (var item in Tree(node)) yield return item; }
    }
    internal static void Run() {
        string root = Path.Combine(Path.GetTempPath(), "claude-tutorial-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Fixture(root); Console.WriteLine("TUTORIAL PASS: " + checks + " checks (synthetic state only)"); }
        finally { Directory.Delete(root, true); }
    }
    static void Fixture(string root) {
        string settings = Path.Combine(root, "settings.json"), seen = settings + ".tutorialSeen";
        var state = new List<Instance> { new Instance { Number = 1, ProcessId = 111 } };
        // 只自动播放一次；登录保护时推迟，不占用这一次。
        var protect = new LauncherUI(new LauncherSettings(), settings); protect.Snapshot(0, state);
        typeof(LauncherUI).GetField("loginAccount", Flags).SetValue(protect, 3);
        Check(!protect.StartTutorialIfFirstRun() && !File.Exists(seen), "login protection postpones the tour without using it up");
        var ui = new LauncherUI(new LauncherSettings(), settings);
        int launches = 0, asks = 0; ui.LaunchProvider = (n, path, accepted) => { launches++; return 1; }; ui.AskOverride = (t, d, b, c, s) => { asks++; return c; };
        ui.ScanProvider = () => { throw new InvalidOperationException("the tour must not scan"); };
        var host = (FrameworkElement)ui.Snapshot(1, state);
        Check(ui.StartTutorialIfFirstRun() && File.Exists(seen), "first run plays and records immediately");
        Check(Field<Canvas>(ui, "tutorialOverlay") != null && Field<int>(ui, "page") == 0, "starts at the welcome step on the instances page");
        Check(StepCount(ui) == 8, "eight steps with an account");
        Check(!ui.StartTutorialIfFirstRun(), "already running");
        ui.CloseTutorial(); Check(Field<int>(ui, "page") == 1 && Field<Canvas>(ui, "tutorialOverlay") == null, "skip returns to the page it started from");
        Check(!ui.StartTutorialIfFirstRun(), "skipping still counts as seen");
        var later = new LauncherUI(new LauncherSettings(), settings); later.Snapshot(0, state);
        Check(!later.StartTutorialIfFirstRun(), "not shown again after restart");
        var empty = new LauncherUI(new LauncherSettings(), Path.Combine(root, "other.json")); empty.Snapshot(0, new List<Instance>());
        empty.StartTutorial(); Check(StepCount(empty) == 7, "the account step is omitted without accounts"); empty.CloseTutorial();
        Check(!File.Exists(Path.Combine(root, "other.json.tutorialSeen")), "manual playback does not touch the first-run record");

        // 按键：回车／→ 下一步，← 上一步，Esc 跳过；其他键全部吃掉。
        ui.Snapshot(1, state); ui.StartTutorial();
        Check(ui.TutorialKey(Key.Return) && Field<int>(ui, "tutorialIndex") == 1, "Return advances");
        Check(ui.TutorialKey(Key.Right) && Field<int>(ui, "tutorialIndex") == 2, "Right advances");
        Check(ui.TutorialKey(Key.Left) && Field<int>(ui, "tutorialIndex") == 1, "Left goes back");
        ui.TutorialKey(Key.Left); ui.TutorialKey(Key.Left); Check(Field<int>(ui, "tutorialIndex") == 0, "Left stops at the first step");
        Check(ui.TutorialKey(Key.D1) && ui.TutorialKey(Key.Tab) && ui.TutorialKey(Key.Space) && Field<int>(ui, "tutorialIndex") == 0, "other keys are swallowed");
        Check(!ui.TutorialKey(Key.System), "system combinations such as Alt+F4 stay");
        // 遮罩盖住全部控件：高亮处的控件也点不到。每一步高亮在页内，气泡不遮挡高亮。
        var overlay = Field<Canvas>(ui, "tutorialOverlay");
        var titles = new List<string>();
        for (int i = 0; i < 8; i++) {
            Check(Field<int>(ui, "tutorialIndex") == i, "walks through step " + i);
            var spot = (Rect)typeof(LauncherUI).GetMethod("TutorialSpot", Flags).Invoke(ui, null);
            var card = Field<Border>(ui, "tutorialCard"); var frame = new Rect(Canvas.GetLeft(card), Canvas.GetTop(card), card.DesiredSize.Width, card.DesiredSize.Height);
            Check(new Rect(0, 0, 1080, 720).Contains(frame), "card stays inside the window at step " + i);
            if (i > 0) { Check(!spot.IsEmpty && spot.Width > 0, "step highlights a real control: " + i); Check(!spot.IntersectsWith(frame), "card does not cover the highlight at step " + i); }
            // 窗口未显示时 WPF 不做命中测试，这里核对结构：铺满、可命中、在最上层；高亮处没有挖空输入。真实点击见 STATUS。
            Check(overlay.ActualWidth == 1080 && overlay.ActualHeight == 720 && overlay.Background != null && overlay.IsHitTestVisible && Panel.GetZIndex(overlay) > ((Panel)overlay.Parent).Children.Cast<UIElement>().Where(c => c != overlay).Max(c => Panel.GetZIndex(c)), "the overlay covers every control at step " + i);
            ui.TutorialKey(Key.Return);
        }
        Check(Field<Canvas>(ui, "tutorialOverlay") == null && Field<int>(ui, "page") == 1, "the last step closes and restores the page");

        // 引导期间托盘菜单或全局快捷键也不能启动账号。
        ui.StartTutorial();
        var select = (Task)typeof(LauncherUI).GetMethod("Select", Flags).Invoke(ui, new object[] { 1 });
        Check(select.IsCompleted && launches == 0 && asks == 0 && Field<HashSet<int>>(ui, "checking").Count == 0, "account selection is ignored during the tour");
        // 永久入口：选择窗默认没有步骤，Esc 关闭。
        ui.CloseTutorial(); ui.ShowTutorialPicker();
        Check(typeof(LauncherUI).GetField("tutorialSteps", Flags).GetValue(ui) == null && Field<Canvas>(ui, "tutorialOverlay") != null, "picker opens the overlay without steps");
        ui.TutorialKey(Key.Return); Check(Field<Canvas>(ui, "tutorialOverlay") != null, "Return does nothing in the picker");
        ui.TutorialKey(Key.Escape); Check(Field<Canvas>(ui, "tutorialOverlay") == null, "Esc closes the picker");
        Check(System.Windows.Automation.AutomationProperties.GetName(Field<Button>(ui, "tutorialEntry")) == "使用教程", "permanent entry in the sidebar");
        Check(launches == 0 && asks == 0, "nothing started or asked during the tour");

        // 气泡定位。
        int side; var bounds = new Rect(0, 0, 1080, 720); var size = new Size(400, 200);
        Check(Tutorial.CardRect(bounds, Rect.Empty, size, out side) == new Rect(340, 260, 400, 200) && side == 0, "no highlight: centered");
        Tutorial.CardRect(bounds, new Rect(100, 100, 100, 40), size, out side); Check(side == 1, "below first");
        Tutorial.CardRect(bounds, new Rect(100, 600, 100, 40), size, out side); Check(side == 2, "above when there is no room below");
        Tutorial.CardRect(bounds, new Rect(0, 0, 400, 720), size, out side); Check(side == 3, "right of a tall highlight");
        Tutorial.CardRect(bounds, new Rect(600, 0, 480, 720), size, out side); Check(side == 4, "left of a tall highlight on the right");
        var forced = Tutorial.CardRect(bounds, bounds, size, out side); Check(side == 0 && bounds.Contains(forced), "a full-window highlight keeps the card inside");

        // 文档：Windows 自己的内容，只有内嵌图片，没有链接和网页控件。
        foreach (bool english in new[] { false, true }) {
            string markdown = Tutorial.Guide(english);
            Check(markdown != null && markdown.Contains("Ctrl+1"), "Windows guide is embedded");
            foreach (string mac in new[] { "⌘", "Finder", "Dock", "Mac" }) Check(!markdown.Contains(mac), "no macOS wording: " + mac);
            var document = Tutorial.Document(markdown);
            Check(Tree(document).OfType<Image>().Count() == 3 && Tree(document).OfType<Image>().All(i => i.Source != null), "three embedded pictures");
            Check(!Tree(document).OfType<Hyperlink>().Any(), "no links");
        }
        var hostile = Tutorial.Document("![x](https://example.invalid/a.jpg)\n![y](C:\\Windows\\a.jpg)\n[链接](https://example.invalid)\n**粗**体");
        Check(!Tree(hostile).OfType<Image>().Any() && !Tree(hostile).OfType<Hyperlink>().Any() && Tree(hostile).OfType<Bold>().Count() == 1, "unknown images are skipped and links stay text");
        var viewer = ui.BuildTutorialDocument();
        Check(viewer.Content is FlowDocumentScrollViewer && !Tree(viewer).Any(n => n.GetType().Name.Contains("WebBrowser") || n.GetType().Name.Contains("WebView")), "the guide uses a native text control");
    }
}
