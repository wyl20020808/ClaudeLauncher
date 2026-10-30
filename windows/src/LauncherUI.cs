using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace ClaudeLauncher {
    sealed class LogEntry { public string Time, Level, Instance, Text; public DateTime At; }

    // 水墨浅色界面，布局坐标与 macOS 1.6 四个页面一致（主区域 808×720，侧栏 272）。
    public sealed class LauncherUI {
        public const string Title = "Claude 多开启动器";
        const string Paper = "#FAF6F0", Ink = "#2B2622", Muted = "#6E655C", Faint = "#A59B90", Brown = "#A8765A";
        static readonly string[][] Palette = { new[] { "#FCE7D4", "#C0743A" }, new[] { "#E9E3FA", "#6A55C8" }, new[] { "#DDF0E6", "#3C8A68" }, new[] { "#FCE3DD", "#C75F47" } };
        static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        sealed class Card { public Border Root; public Ellipse Dot; public TextBlock Title, Status, Code, Folder; public Button Action; }
        sealed class Row { public Border Root; public Ellipse Dot; public TextBlock Name, Status, Recent; public CheckBox Select; }

        // 账号页：本地别名、搜索与状态筛选、可见选择（SPEC 账号与日志交互）。
        Dictionary<int, string> accountNames = new Dictionary<int, string>();
        string namesError;
        readonly string namesPath;
        readonly HashSet<int> selectedAccounts = new HashSet<int>();
        TextBox accountSearch; ComboBox accountStatus; CheckBox selectAll; Button copySelected; TextBlock accountsEmpty;
        static readonly string[] StatusFilters = { "全部状态", "运行中", "未运行", "处理中", "异常" };
        // 日志页：组合筛选与分页。
        TextBox logSearch; ComboBox logTime, logInstance, logLevel; Button logPrev, logNext; TextBlock logPageLabel;
        int logPageIndex;

        readonly Window window;
        readonly Grid[] pages = new Grid[4];
        readonly Button[] nav = new Button[4];
        readonly Border indicator;
        readonly TextBlock summary, message, emptyHint, tableFooter, logSummary;
        readonly TextBlock[] stats = new TextBlock[3];
        readonly Button create, endLogin;
        Button update;
        // 已保存的设置与编辑副本；每次更改即时保存，失败时副本回到已保存值（SPEC 系统设置）。
        LauncherSettings settings, draft;
        readonly string settingsPath, lastPagePath;
        // 水墨插画元素（侧栏、背景、卡片），按“显示水墨插画”切换。
        readonly List<UIElement> artwork = new List<UIElement>();
        Border toast; TextBlock toastText;
        readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        readonly StackPanel cardList, rowList;
        readonly Canvas logRows;
        readonly Dictionary<int, Card> cards = new Dictionary<int, Card>();
        readonly Dictionary<int, Row> rows = new Dictionary<int, Row>();
        readonly List<LogEntry> logs = new List<LogEntry>();

        readonly Dictionary<int, DateTime> pending = new Dictionary<int, DateTime>();
        readonly HashSet<int> checking = new HashSet<int>();
        readonly Dictionary<int, string> errors = new Dictionary<int, string>();
        readonly Dictionary<int, LaunchRecord> launched = new Dictionary<int, LaunchRecord>();
        readonly HashSet<int> incompleteDeletes = new HashSet<int>();
        readonly DispatcherTimer timer = new DispatcherTimer();
        readonly ProcessWatch processWatch = new ProcessWatch();
        List<Instance> instances = new List<Instance>();
        int loginAccount, page;
        string executable, scanError, lastProcessState = "", lastRefreshError = "", lastLogged;
        bool scanning, closed, healthy, deleting, updating;
        DateTime nextHeartbeat = DateTime.MinValue;

        static SolidColorBrush B(string hex) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); brush.Freeze(); return brush; }
        static string Two(int number) { return number.ToString("00"); }
        static TextBlock T(string text, double size, string color, FontWeight weight) {
            return new TextBlock { Text = text, FontSize = size, Foreground = B(color), FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis };
        }
        static TextBlock Glyph(string code, double size, string color) {
            return new TextBlock { Text = code, FontFamily = Icons, FontSize = size, Foreground = B(color), VerticalAlignment = VerticalAlignment.Center };
        }
        static T At<T>(Canvas canvas, T element, double x, double y, double width = Double.NaN, double height = Double.NaN) where T : FrameworkElement {
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y);
            if (!Double.IsNaN(width)) element.Width = width;
            if (!Double.IsNaN(height)) element.Height = height;
            canvas.Children.Add(element); return element;
        }
        // 圆角半透明面板：表格、统计卡、设置组共用，透出页面背景。
        static Border Panel(double radius, string fill) {
            return new Border { CornerRadius = new CornerRadius(radius), Background = B(fill), BorderBrush = B("#EDE4D8"), BorderThickness = new Thickness(1) };
        }
        // 静态占位输入框：只做外观，不可交互。
        static Border Field(Canvas canvas, double x, double y, double width, double height, string icon, string text, string color, bool chevron) {
            var grid = new Grid { Margin = new Thickness(14, 0, 12, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (icon != null) { var glyph = Glyph(icon, 13, Muted); glyph.Margin = new Thickness(0, 0, 10, 0); grid.Children.Add(glyph); }
            var label = T(text, 13, color, FontWeights.Normal); label.VerticalAlignment = VerticalAlignment.Center; label.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(label, 1); grid.Children.Add(label);
            if (chevron) { var down = Glyph("", 10, Muted); Grid.SetColumn(down, 2); grid.Children.Add(down); }
            var box = Panel(10, "#EBFFFDFA"); box.Child = grid; box.IsHitTestVisible = false;
            return At(canvas, box, x, y, width, height);
        }
        Button Action(string text, string style, string icon, double size) {
            var button = new Button { Style = (Style)window.FindResource(style), FontSize = size };
            if (icon == null) button.Content = text;
            else {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                var glyph = Glyph(icon, size - 1, Ink); glyph.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
                glyph.Margin = new Thickness(0, 0, 8, 0);
                content.Children.Add(glyph); content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                button.Content = content;
            }
            AutomationProperties.SetName(button, text.Replace("+", "").Trim());
            return button;
        }
        Button MoreButton(int number) {
            var more = new Button { Style = (Style)window.FindResource("Icon"), Content = Glyph("", 14, Faint), ToolTip = "更多" };
            AutomationProperties.SetName(more, "账号 " + Two(number) + " 更多操作");
            more.Click += delegate { ShowMore(number, more); };
            return more;
        }
        static ImageSource Art(string name) {
            try {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeLauncher.UI." + name)) {
                    if (stream == null) return null;
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
                    return image;
                }
            } catch (Exception error) { Diagnostics.Error("ui_art_error", error); return null; }
        }

        public LauncherUI() : this(LauncherSettings.Load(LauncherSettings.DefaultPath), LauncherSettings.DefaultPath) { }
        // 测试与离屏预览传入合成设置和临时路径，不读写用户的真实设置。
        internal LauncherUI(LauncherSettings initial, string path) {
            settings = initial; draft = initial.Clone(); settingsPath = path;
            lastPagePath = IOPath.Combine(IOPath.GetDirectoryName(path), IOPath.GetFileName(LauncherSettings.LastPagePath));
            namesPath = IOPath.Combine(IOPath.GetDirectoryName(path), IOPath.GetFileName(AccountNames.DefaultPath));
            try { accountNames = AccountNames.Load(namesPath); } catch (Exception error) { namesError = error.Message; }
            for (int i = 1; i <= 10; i++) if (DeletionJournal.IsPending(i, ProfileDeletion.Local)) incompleteDeletes.Add(i);
            loginAccount = LoginProtection.Read(ProfileDeletion.Local);
            window = new Window {
                Title = Title, Background = B(Paper), FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"), FontSize = 13, Foreground = B(Ink),
                WindowStartupLocation = WindowStartupLocation.CenterScreen, UseLayoutRounding = true, SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.CanMinimize, MaxWidth = SystemParameters.WorkArea.Width, MaxHeight = SystemParameters.WorkArea.Height
            };
            window.Resources = (ResourceDictionary)XamlReader.Parse(Styles);
            TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeLauncher.Icon")) {
                if (icon == null) throw new InvalidOperationException("启动器缺少内嵌图标。");
                var decoder = new IconBitmapDecoder(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                window.Icon = decoder.Frames[0];
                logo = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            }
            var root = new Grid { Width = 1080, Height = 720, ClipToBounds = true };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(272) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            window.Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            // 侧栏
            var sidebar = new Canvas { Background = B("#F3ECE2") };
            root.Children.Add(sidebar);
            ImageSource mountains = Art("sidebar");
            if (mountains != null) {
                double height = 272 * mountains.Height / mountains.Width;
                var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0)); mask.GradientStops.Add(new GradientStop(Colors.Black, Math.Min(1, 180 / height)));
                artwork.Add(At(sidebar, new Image { Source = mountains, Stretch = Stretch.Fill, OpacityMask = mask, IsHitTestVisible = false }, 0, 720 - height, 272, height));
            }
            At(sidebar, new Rectangle { Fill = B("#E6DCCF") }, 271, 0, 1, 720);
            At(sidebar, new Image { Source = logo }, 32, 54, 58, 58);
            var brand = At(sidebar, T("Claude", 40, Ink, FontWeights.Normal), 98, 42);
            brand.FontFamily = new FontFamily("Georgia, Cambria, Times New Roman");
            At(sidebar, T("多开启动器", 17, Ink, FontWeights.Medium), 100, 96);
            At(sidebar, T("DESKTOP · V" + Updates.DisplayVersion, 9, Muted, FontWeights.Normal), 101, 126);
            string[][] items = { new[] { "实例管理", "" }, new[] { "账号管理", "" }, new[] { "日志记录", "" }, new[] { "系统设置", "" } };
            for (int i = 0; i < 4; i++) {
                int index = i;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                var glyph = Glyph(items[i][1], 19, "#3A332D"); glyph.Width = 36; content.Children.Add(glyph);
                content.Children.Add(T(items[i][0], 16, Ink, FontWeights.Medium));
                nav[i] = At(sidebar, new Button { Style = (Style)window.FindResource("Nav"), Content = content }, 20, 176 + i * 56, 232, 48);
                AutomationProperties.SetName(nav[i], items[i][0]);
                nav[i].Click += delegate { ShowPage(index); RememberPage(); };
            }
            indicator = At(sidebar, new Border { Background = B("#B9825F"), CornerRadius = new CornerRadius(2) }, 6, 188, 4, 24);

            // 主区域背景：画面靠右，左侧洗白以保证文字可读。
            var main = new Grid { ClipToBounds = true };
            Grid.SetColumn(main, 1); root.Children.Add(main);
            ImageSource backdrop = Art("background");
            if (backdrop != null) {
                var image = new Image { Source = backdrop, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                main.Children.Add(image); artwork.Add(image);
            }
            var wash = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            wash.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#EDFAF6F0"), 0));
            wash.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#B8FAF6F0"), 1));
            main.Children.Add(new Rectangle { Fill = wash, IsHitTestVisible = false });
            for (int i = 0; i < 4; i++) { pages[i] = new Grid(); main.Children.Add(pages[i]); }
            // 窗口内提示条（SPEC 消息通知 [Windows]）：主区底部，约 5 秒后隐藏，不拦截点击。
            toastText = new TextBlock { FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
            toast = new Border { Child = toastText, Background = B("#E62B2622"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 9, 16, 9),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 22), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            AutomationProperties.SetAutomationId(toast, "LauncherToast");
            main.Children.Add(toast);
            toastTimer.Tick += delegate { toastTimer.Stop(); toast.Visibility = Visibility.Collapsed; };

            // 实例管理
            var instancesPage = new Canvas(); pages[0].Children.Add(instancesPage);
            var slogan = At(instancesPage, T("让多个 Claude，服务你的不同灵感", 28, "#3A332D", FontWeights.Normal), 40, 54);
            slogan.FontFamily = new FontFamily("KaiTi, STKaiti, Microsoft YaHei UI");
            At(instancesPage, new System.Windows.Shapes.Path { Data = Geometry.Parse("M196,106 C260,102 330,94 388,96"), Stroke = B("#D9EE9A63"), StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false }, 0, 0);
            At(instancesPage, new System.Windows.Shapes.Path { Data = Geometry.Parse("M232,110 C280,106 320,103 360,103"), Stroke = B("#80EE9A63"), StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false }, 0, 0);
            summary = At(instancesPage, T("正在读取状态", 12, Muted, FontWeights.Normal), 44, 116, 400);
            create = At(instancesPage, Action("+  新建实例", "Pill", null, 16), 612, 52, 160, 48);
            AutomationProperties.SetAutomationId(create, "NewInstance");
            create.Click += async delegate { await NewInstance(); };
            cardList = new StackPanel();
            At(instancesPage, new ScrollViewer { Content = cardList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 0, 0) }, 16, 140, 792, 528);
            emptyHint = At(instancesPage, T("本机还没有 Claude 账号目录。点击右上角「+ 新建实例」创建第一个。", 15, Muted, FontWeights.Normal), 40, 180, 700);
            message = At(instancesPage, new TextBlock { FontSize = 12, Foreground = B(Muted), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis }, 24, 674, 600, 36);
            AutomationProperties.SetAutomationId(message, "LauncherFeedback");
            endLogin = At(instancesPage, Action("结束登录保护", "Btn", null, 13), 640, 676, 140, 30);
            endLogin.Click += async delegate { await EndLogin(); };
            for (int n = 1; n <= 10; n++) { cards[n] = BuildCard(n); cardList.Children.Add(cards[n].Root); }

            // 账号管理
            var accounts = PageTitled(1, "账号管理", "统一管理本机 Claude 账号空间，查看对应实例、运行状态与最近使用记录。");
            var add = At(accounts, Action("+  添加账号", "Pill", null, 16), 612, 56, 160, 44);
            add.Click += async delegate { await NewInstance(); };
            string[][] statInfo = { new[] { "账号总数", "本机已有账号空间", Ink }, new[] { "运行中", "主进程正在运行", "#3C8A68" }, new[] { "需要处理", "启动失败或检查未通过", "#C7704A" } };
            for (int i = 0; i < 3; i++) {
                double x = 36 + i * 248;
                At(accounts, Panel(16, "#DBFFFDFA"), x, 140, 240, 84);
                At(accounts, T(statInfo[i][0], 12, Muted, FontWeights.Normal), x + 20, 154);
                stats[i] = At(accounts, T("00", 30, statInfo[i][2], FontWeights.Medium), x + 18, 170);
                At(accounts, T(statInfo[i][1], 11, Muted, FontWeights.Normal), x + 80, 190, 150);
            }
            accountSearch = SearchBox(accounts, 36, 240, 268, 38, "搜索别名、编号或目录…", "搜索账号");
            accountSearch.TextChanged += delegate { Render(); };
            accountStatus = Choice(accounts, 316, 240, 130, 38, StatusFilters, "账号状态筛选");
            accountStatus.SelectionChanged += delegate { Render(); };
            copySelected = At(accounts, Action("复制选中路径", "Btn", "", 13), 516, 240, 134, 38);
            AutomationProperties.SetAutomationId(copySelected, "CopySelectedPaths");
            copySelected.Click += delegate { CopySelectedPaths(); };
            var refresh = At(accounts, Action("刷新状态", "Btn", "", 14), 660, 240, 112, 38);
            refresh.Click += async delegate { executable = null; await ScanNow(); };
            At(accounts, Panel(16, "#DBFFFDFA"), 36, 292, 736, 404);
            selectAll = At(accounts, new CheckBox { IsThreeState = false, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center }, 60, 307);
            AutomationProperties.SetName(selectAll, "全选当前结果");
            selectAll.Click += delegate { ToggleSelectAll(); };
            string[] heads = { "账号名称", "登录邮箱", "对应实例", "状态", "最近使用", "操作" };
            double[] offsets = { 100, 250, 416, 478, 566, 660 };
            for (int i = 0; i < heads.Length; i++) At(accounts, T(heads[i], 13, "#7A6F64", FontWeights.SemiBold), 36 + offsets[i], 304);
            At(accounts, new Rectangle { Fill = B("#EFE7DC") }, 37, 336, 734, 1);
            At(accounts, new Rectangle { Fill = B("#EFE7DC") }, 37, 655, 734, 1);
            rowList = new StackPanel();
            At(accounts, new ScrollViewer { Content = rowList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 37, 337, 734, 318);
            for (int n = 1; n <= 10; n++) { rows[n] = BuildRow(n); rowList.Children.Add(rows[n].Root); }
            accountsEmpty = At(accounts, T("没有符合条件的账号", 13, "#8A7F74", FontWeights.Normal), 37, 470, 734);
            accountsEmpty.TextAlignment = TextAlignment.Center;
            tableFooter = At(accounts, T("", 12, Muted, FontWeights.Normal), 60, 667, 400);
            var note = At(accounts, T("登录信息由各实例独立管理", 12, Muted, FontWeights.Normal), 500, 667, 248);
            note.TextAlignment = TextAlignment.Right;

            // 日志记录
            var logsPage = PageTitled(2, "日志记录", "查看实例启动、关闭与异常信息，便于追踪运行情况和排查问题。");
            var export = At(logsPage, Action("导出日志", "Btn", "", 14), 502, 56, 128, 44);
            export.Click += delegate { ExportLogs(); };
            var clear = At(logsPage, Action("清空日志", "Btn", "", 14), 640, 56, 132, 44);
            clear.Click += delegate {
                if (logs.Count > 0 && Ask("清空日志？", "只清空启动器内存中的本次运行日志，不影响本地诊断日志、Claude 或账号数据。", new[] { "取消", "清空" }, 0, 0) == 1) { logs.Clear(); logPageIndex = 0; RenderLogs(); }
            };
            logTime = Choice(logsPage, 36, 140, 170, 40, new[] { "本次运行", "最近 5 分钟", "最近 1 小时" }, "日志时间范围");
            logInstance = Choice(logsPage, 216, 140, 150, 40, new[] { "全部实例", "系统" }.Concat(Enumerable.Range(1, 10).Select(n => "Claude " + n)).ToArray(), "日志实例筛选");
            logLevel = Choice(logsPage, 376, 140, 150, 40, new[] { "全部级别", "信息", "警告", "错误" }, "日志级别筛选");
            logSearch = SearchBox(logsPage, 552, 140, 220, 40, "搜索日志内容…", "搜索日志");
            foreach (ComboBox box in new[] { logTime, logInstance, logLevel }) box.SelectionChanged += delegate { logPageIndex = 0; RenderLogs(); };
            logSearch.TextChanged += delegate { logPageIndex = 0; RenderLogs(); };
            At(logsPage, Panel(16, "#DBFFFDFA"), 36, 196, 736, 494);
            string[] logHeads = { "时间", "级别", "实例", "事件内容" };
            double[] logOffsets = { 24, 196, 290, 404 };
            for (int i = 0; i < 4; i++) At(logsPage, T(logHeads[i], 13, "#7A6F64", FontWeights.SemiBold), 36 + logOffsets[i], 209);
            At(logsPage, new Rectangle { Fill = B("#EFE7DC") }, 37, 240, 734, 1);
            At(logsPage, new Rectangle { Fill = B("#EFE7DC") }, 37, 640, 734, 1);
            logRows = At(logsPage, new Canvas(), 36, 241, 736, 399);
            logPrev = At(logsPage, Action("上一页", "Btn", null, 12), 60, 650, 76, 30);
            AutomationProperties.SetAutomationId(logPrev, "LogPrevious");
            logPrev.Click += delegate { logPageIndex--; RenderLogs(); };
            logPageLabel = At(logsPage, T("第 1 / 1 页", 12, Muted, FontWeights.Normal), 146, 657, 100);
            logPageLabel.TextAlignment = TextAlignment.Center;
            logNext = At(logsPage, Action("下一页", "Btn", null, 12), 256, 650, 76, 30);
            AutomationProperties.SetAutomationId(logNext, "LogNext");
            logNext.Click += delegate { logPageIndex++; RenderLogs(); };
            logSummary = At(logsPage, T("", 12, Muted, FontWeights.Normal), 448, 658, 300);
            logSummary.TextAlignment = TextAlignment.Right;

            // 系统设置：六个分类，修改先进入草稿，保存后写入启动器自己的 settings.json（SPEC 系统设置 [Windows]）。
            BuildSettings(PageTitled(3, "系统设置", "调整启动器自身的行为、外观、本地数据与通知；不改变任何 Claude 配置。"));

            // SPEC 启动行为：只恢复启动器自己的页面，不启动或激活任何 Claude。
            string first = settings.RememberPage ? LauncherSettings.ReadLastPage(lastPagePath) ?? settings.StartPage : settings.StartPage;
            ShowPage(Array.IndexOf(LauncherSettings.Pages, first));
            AddLog("启动器已完成初始化");
            if (namesError != null) AddLog("账号别名文件无法读取，已停止写入别名：" + namesError);
            if (settings.LoadError != null) { Diagnostics.Write("settings_load_error", settings.LoadError); AddLog("设置文件无法读取，已按默认设置运行：" + settings.LoadError); }
            ApplySettings();
            timer.Tick += async delegate { await Refresh(); };
            window.Loaded += async delegate {
                await Refresh(); timer.Start();
                // SPEC 网络边界：已保存开启时，每次打开启动器检查一次；只在有新版时提示，失败只记日志。
                if (!closed && settings.AllowUpdates) await CheckUpdate(false);
            };
            window.Closed += delegate { closed = true; timer.Stop(); processWatch.Dispose(); Diagnostics.Write("window_closed", "正常关闭启动器"); };
            window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (deleting) { e.Cancel = true; Message("请先完成或取消当前删除操作。", true); return; }
                // 正式运行（Show 建立托盘后）：开启“关闭窗口后继续运行”时只隐藏；关闭该设置时关闭窗口即退出启动器（走统一的退出流程）。不退出 Claude。
                if (tray == null || exiting) return;
                e.Cancel = true;
                if (!settings.CloseToBackground) { window.Dispatcher.BeginInvoke(new Action(() => ExitLauncher())); return; }
                window.Hide();
                if (!trayHintShown && tray.Visible) {
                    trayHintShown = true;
                    tray.ShowBalloonTip(4000, Title, "启动器仍在通知区域运行。左键单击图标打开窗口，右键可退出。", System.Windows.Forms.ToolTipIcon.Info);
                }
            };
            window.SourceInitialized += delegate {
                var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                source.AddHook(ExitHook);
            };
            Application.Current.SessionEnding += delegate { exiting = true; };
        }

        // ---- 常驻图标（通知区域） ----
        System.Windows.Forms.NotifyIcon tray;
        bool exiting, trayHintShown;
        // 安装程序用此消息请求启动器退出（WM_CLOSE 现在只是隐藏到通知区域）。
        public static readonly int ExitMessage = Native.RegisterWindowMessage("ClaudeMultiLauncher.Exit");
        IntPtr ExitHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == ExitMessage) { handled = true; if (!deleting) ExitLauncher(); }
            return IntPtr.Zero;
        }
        void CreateTray() {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeLauncher.Icon"))
                tray = new System.Windows.Forms.NotifyIcon { Icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize), Text = Title, Visible = settings.ShowTray };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Opening += delegate { BuildTrayMenu(menu); };
            menu.Items.Add("…");
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, System.Windows.Forms.MouseEventArgs e) { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowMain(); };
            tray.DoubleClick += delegate { ShowMain(); };
        }
        void ShowMain() {
            if (closed) return;
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
        // 菜单每次打开都重新扫描进程；扫描失败时账号项禁用并显示原因。不显示任何账号内容。
        void BuildTrayMenu(System.Windows.Forms.ContextMenuStrip menu) {
            string failure = null;
            try { Apply(Core.Scan()); healthy = true; scanError = null; }
            catch (Exception error) { failure = error.Message; healthy = false; scanError = error.Message; }
            Render();
            menu.Items.Clear();
            var show = new System.Windows.Forms.ToolStripMenuItem("显示启动器窗口");
            show.Font = new System.Drawing.Font(show.Font, System.Drawing.FontStyle.Bold);
            show.Click += delegate { ShowMain(); };
            menu.Items.Add(show);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            if (failure != null) {
                menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("无法读取账号状态：" + (failure.Length > 60 ? failure.Substring(0, 60) + "…" : failure)) { Enabled = false, ToolTipText = failure });
            } else {
                var entries = TrayAccounts();
                if (entries.Count == 0) menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("本机还没有账号") { Enabled = false });
                foreach (var entry in entries) {
                    int number = entry.Item1;
                    var item = new System.Windows.Forms.ToolStripMenuItem(entry.Item2) { Enabled = entry.Item3 };
                    item.Click += async delegate { if (incompleteDeletes.Contains(number)) await Delete(number); else await Select(number); };
                    menu.Items.Add(item);
                }
            }
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var check = new System.Windows.Forms.ToolStripMenuItem("检查更新…") { Enabled = !updating };
            check.Click += async delegate { ShowMain(); await CheckUpdate(true); };
            menu.Items.Add(check);
            var exit = new System.Windows.Forms.ToolStripMenuItem("退出启动器");
            exit.ToolTipText = "只退出启动器，不退出任何 Claude。";
            exit.Click += delegate { ExitLauncher(); };
            menu.Items.Add(exit);
        }
        // 托盘中的账号项：与主界面卡片同一份可见性与可用状态（编号、标签、是否可点）。
        internal List<Tuple<int, string, bool>> TrayAccounts() {
            var result = new List<Tuple<int, string, bool>>();
            for (int n = 1; n <= 10; n++) {
                if (cards[n].Root.Visibility != Visibility.Visible) continue;
                string state = incompleteDeletes.Contains(n) ? "删除未完成" : checking.Contains(n) ? "检查中" : pending.ContainsKey(n) ? "启动中" : Core.Running(n, instances) ? "运行中" : "未运行";
                result.Add(Tuple.Create(n, "账号 " + Two(n) + " · " + state, cards[n].Action.IsEnabled));
            }
            return result;
        }
        void ExitLauncher() {
            if (deleting) { ShowMain(); Message("请先完成或取消当前删除操作。", true); return; }
            exiting = true;
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            window.Close();
            Application.Current.Shutdown();
        }
        readonly BitmapFrame logo;

        Canvas PageTitled(int index, string title, string subtitle) {
            var canvas = new Canvas(); pages[index].Children.Add(canvas);
            var heading = At(canvas, T(title, 32, Ink, FontWeights.Bold), 36, 50);
            heading.FontFamily = new FontFamily("Source Han Serif SC, Noto Serif SC, SimSun, Microsoft YaHei UI");
            At(canvas, T(subtitle, 13, Muted, FontWeights.Normal), 37, 104, 600);
            return canvas;
        }
        Card BuildCard(int n) {
            var card = new Card();
            var canvas = new Canvas { Width = 776, Height = 124 };
            card.Root = new Border { Width = 776, Height = 124, Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(14), Background = B("#FFFCF7"), BorderBrush = B("#EDE4D8"), BorderThickness = new Thickness(1), Child = canvas, HorizontalAlignment = HorizontalAlignment.Left };
            ImageSource art = Art("card-" + ((n - 1) % 4 + 1));
            if (art != null) {
                artwork.Add(At(canvas, new Image { Source = art, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 200, 110), 12, 12), IsHitTestVisible = false }, 185, 6, 200, 110));
                var fade = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                fade.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFFCF7"), 0)); fade.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#00FFFCF7"), 1));
                artwork.Add(At(canvas, new Rectangle { Fill = fade, IsHitTestVisible = false }, 184, 6, 90, 110));
            }
            string[] palette = Palette[(n - 1) % 4];
            var badge = At(canvas, new Grid(), 22, 39, 44, 44);
            badge.Children.Add(new Ellipse { Fill = B(palette[0]), Stroke = B("#40" + palette[1].Substring(1)), StrokeThickness = 1 });
            badge.Children.Add(new TextBlock { Text = n.ToString(), FontSize = 20, FontWeight = FontWeights.Medium, Foreground = B(palette[1]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            card.Title = At(canvas, T(DisplayName(n), 18, Ink, FontWeights.SemiBold), 84, 16, 300);
            card.Dot = At(canvas, new Ellipse { Fill = B("#B9B1A7") }, 86, 51, 9, 9);
            card.Status = At(canvas, T("读取状态…", 13, Muted, FontWeights.Normal), 102, 44, 150);
            var chips = At(canvas, new StackPanel { Orientation = Orientation.Horizontal }, 84, 80);
            card.Folder = Chip(chips, n == 1 ? "默认目录" : IOPath.GetFileName(Core.Folder(n, Core.Root)).Replace("Claude-", ""));
            card.Code = Chip(chips, Core.CodeState(n, new Instance[0], null));
            string[][] quota = { new[] { "", "5小时限额" }, new[] { "", "周限额" } };
            for (int row = 0; row < 2; row++) {
                double y = 18 + row * 46;
                At(canvas, Glyph(quota[row][0], 16, Muted), 406, y + 1);
                At(canvas, T(quota[row][1], 14, Ink, FontWeights.Medium), 436, y);
                At(canvas, new Border { CornerRadius = new CornerRadius(3), Background = B("#EEE8E0") }, 436, y + 26, 150, 6);
                var none = At(canvas, T("未接入", 12, Faint, FontWeights.Normal), 592, y + 19, 44); none.TextAlignment = TextAlignment.Right;
            }
            int number = n;
            card.Action = At(canvas, Action("启动", "Btn", null, 17), 646, 36, 86, 52);
            AutomationProperties.SetAutomationId(card.Action, "Profile" + n);
            card.Action.Click += async delegate {
                if (incompleteDeletes.Contains(number)) await Delete(number); else await Select(number);
            };
            At(canvas, MoreButton(n), 738, 47, 30, 30);
            return card;
        }
        static TextBlock Chip(StackPanel chips, string text) {
            var label = new TextBlock { Text = text, FontSize = 12, Foreground = B(Muted) };
            chips.Children.Add(new Border { Child = label, CornerRadius = new CornerRadius(6), Background = B("#FAF6EF"), BorderBrush = B("#E8DFD3"), BorderThickness = new Thickness(1), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 8, 0) });
            return label;
        }
        Row BuildRow(int n) {
            var row = new Row();
            var canvas = new Canvas { Width = 734, Height = 52 };
            row.Root = new Border { Child = canvas, BorderBrush = B("#EFE7DC"), BorderThickness = new Thickness(0, 0, 0, 1) };
            int number = n;
            row.Select = At(canvas, new CheckBox { Cursor = Cursors.Hand }, 24, 18);
            AutomationProperties.SetName(row.Select, "选择账号 " + Two(n));
            row.Select.Click += delegate { if (row.Select.IsChecked == true) selectedAccounts.Add(number); else selectedAccounts.Remove(number); UpdateSelection(); };
            string[] palette = Palette[(n - 1) % 4];
            var badge = At(canvas, new Border { CornerRadius = new CornerRadius(8), Background = B(palette[0]) }, 64, 12, 28, 28);
            badge.Child = new TextBlock { Text = Two(n), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(palette[1]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Name = At(canvas, T(DisplayName(n), 14, Ink, FontWeights.Medium), 100, 15, 140);
            var email = At(canvas, T("不读取", 13, Faint, FontWeights.Normal), 250, 16, 150);
            email.ToolTip = "启动器不读取登录凭据或账号邮箱。";
            At(canvas, T("Claude " + n, 13, Muted, FontWeights.Normal), 416, 16, 60);
            row.Dot = At(canvas, new Ellipse { Fill = B("#B9B1A7") }, 478, 22, 8, 8);
            row.Status = At(canvas, T("未运行", 13, Muted, FontWeights.SemiBold), 492, 15, 72);
            row.Recent = At(canvas, T("—", 13, Muted, FontWeights.Normal), 566, 16, 84);
            var pencil = At(canvas, new Button { Style = (Style)window.FindResource("Icon"), Content = Glyph("", 12, "#8A7F74"), ToolTip = "设置本地别名", BorderBrush = B("#E6DCCF"), BorderThickness = new Thickness(1), Background = Brushes.White }, 652, 11, 30, 30);
            AutomationProperties.SetName(pencil, "设置账号 " + Two(n) + " 的本地别名");
            pencil.Click += delegate { EditAlias(number); };
            var more = At(canvas, MoreButton(n), 690, 11, 30, 30);
            more.BorderBrush = B("#E6DCCF"); more.BorderThickness = new Thickness(1); more.Background = Brushes.White;
            return row;
        }

        // ---- 系统设置（SPEC 系统设置 [Windows]） ----
        readonly Canvas[] settingsSections = new Canvas[6];
        readonly Button[] settingsTabs = new Button[6];
        readonly List<Action> settingsSync = new List<Action>();
        TextBlock settingsFeedback, autoStartStatus;
        int settingsSection;
        bool syncingSettings;

        void BuildSettings(Canvas page) {
            At(page, Panel(16, "#DBFFFDFA"), 36, 140, 736, 556);
            At(page, new Rectangle { Fill = B("#EDE4D8") }, 206, 141, 1, 554);
            string[][] tabs = { new[] { "常规设置", "" }, new[] { "启动行为", "" }, new[] { "外观设置", "" }, new[] { "数据与存储", "" }, new[] { "消息通知", "" }, new[] { "关于软件", "" } };
            for (int i = 0; i < tabs.Length; i++) {
                int index = i;
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
                var glyph = Glyph(tabs[i][1], 13, "#3A332D"); glyph.Width = 28; line.Children.Add(glyph);
                line.Children.Add(new TextBlock { Text = tabs[i][0], FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                settingsTabs[i] = At(page, new Button { Style = (Style)window.FindResource("Nav"), Content = line, Padding = new Thickness(0), Foreground = B("#3A332D") }, 48, 154 + i * 46, 146, 38);
                AutomationProperties.SetName(settingsTabs[i], tabs[i][0]);
                settingsTabs[i].Click += delegate { ShowSettingsSection(index); };
                settingsSections[i] = At(page, new Canvas { Visibility = Visibility.Collapsed }, 230, 156, 518, 470);
            }
            string data = LauncherSettings.DataDirectory;

            // 常规设置
            var c = settingsSections[0]; double y = Heading(c, "常规设置");
            y = SwitchRow(c, y, "关闭窗口后继续运行", "关闭窗口只隐藏，可从通知区域或再次打开启动器恢复；关闭此项后，关闭窗口即退出启动器。都不会退出 Claude。", () => draft.CloseToBackground, v => draft.CloseToBackground = v);
            y = SwitchRow(c, y, "显示通知区域图标", "右键图标可查看账号状态、检查更新或退出。关闭后再次打开启动器即可显示窗口。", () => draft.ShowTray, v => draft.ShowTray = v);
            Note(c, y + 8, "这些设置只影响本启动器，不改变 Claude 的运行、登录或窗口。");

            // 启动行为
            c = settingsSections[1]; y = Heading(c, "启动行为");
            ActionRow(c, 0, y, "管理开机启动…", ManageAutoStart);
            autoStartStatus = At(c, new TextBlock { FontSize = 12, Foreground = B(Muted), TextWrapping = TextWrapping.Wrap }, 0, y + 42, 518);
            y += 82;
            y = SwitchRow(c, y, "恢复启动器上次页面", "只恢复本启动器的页面，不自动启动或激活任何 Claude。", () => draft.RememberPage, v => draft.RememberPage = v);
            y = ChoiceRow(c, y, "未恢复时的启动页面", new[] { "实例管理", "账号管理", "日志记录", "系统设置" }, LauncherSettings.Pages.Cast<object>().ToArray(), () => draft.StartPage, v => draft.StartPage = (string)v, () => !draft.RememberPage);
            y = ChoiceRow(c, y, "本地进程状态刷新间隔", new[] { "1 秒", "3 秒", "5 秒" }, LauncherSettings.Intervals.Cast<object>().ToArray(), () => draft.RefreshInterval, v => draft.RefreshInterval = (int)v, null);
            SwitchRow(c, y, "点击账号时恢复最小化窗口", "只处理经过核对的目标 PID 窗口；关闭后最小化的窗口需从任务栏打开。", () => draft.RestoreMinimized, v => draft.RestoreMinimized = v);

            // 外观设置
            c = settingsSections[2]; y = Heading(c, "外观设置");
            y = SwitchRow(c, y, "显示水墨插画", "关闭后保留浅色纸张背景和原有文字布局。", () => draft.ShowArtwork, v => draft.ShowArtwork = v);
            Note(c, y + 8, "本地界面用量：Windows 版尚未接入，需先完成调查并取得授权，因此暂不提供开关。\n\n界面语言：简体中文\n主题：浅色水墨\n\n这两项为当前版本的固定样式。Claude 自身的外观、浏览器和登录方式由官方客户端管理。");

            // 数据与存储
            c = settingsSections[3]; y = Heading(c, "数据与存储");
            Note(c, y, "启动器数据目录（设置、日志、登录保护与删除状态）\n" + Diagnostics.Redact(data) + "\n账号目录固定，不迁移、不合并。");
            y += 76;
            ActionRow(c, 0, y, "在资源管理器中显示", () => OpenFolder(data));
            ActionRow(c, 264, y, "复制数据目录路径", () => {
                try { Clipboard.SetText(data); settingsFeedback.Text = "启动器数据目录路径已复制"; } catch (Exception error) { Alert("无法复制路径", error.Message); }
            });
            ActionRow(c, 0, y + 44, "导出已保存的设置…", ExportSettings);
            ActionRow(c, 264, y + 44, "打开本地诊断日志", () => {
                try { Diagnostics.OpenFolder(); } catch (Exception error) { Diagnostics.Error("open_logs_error", error); Alert("无法打开日志目录", error.Message); }
            });
            y = ChoiceRow(c, y + 100, "本次运行日志保留上限", new[] { "100 条", "500 条", "1000 条" }, LauncherSettings.LogLimits.Cast<object>().ToArray(), () => draft.LogLimit, v => draft.LogLimit = (int)v, null);
            Note(c, y + 4, "日志记录页只留在内存，退出即清空；导出位置由你选择。导出的设置不含账号、别名、项目或凭据。恢复默认只重置启动器偏好，保留登录保护、开机启动项和任何 Claude 数据。");

            // 消息通知
            c = settingsSections[4]; y = Heading(c, "消息通知");
            y = SwitchRow(c, y, "显示窗口内提示", "在启动器底部临时显示警告和错误；不推送、不上传、不读取 Claude 消息。", () => draft.LocalNotifications, v => draft.LocalNotifications = v);
            y = SwitchRow(c, y, "警告时播放提示音", "仅在窗口内提示开启时生效，使用系统提示音。", () => draft.NotificationSound, v => draft.NotificationSound = v);
            ActionRow(c, 0, y + 6, "测试已保存的通知设置", () => {
                settingsFeedback.Text = Notify("本地通知测试：设置已生效，没有发送网络请求。") ? "已按当前设置显示提示" : "窗口内提示已关闭；开启后可测试";
            });
            Note(c, y + 56, "安全确认和错误弹窗始终保留，不受通知开关影响。");

            // 关于软件
            c = settingsSections[5]; y = Heading(c, "Claude 多开启动器 " + Updates.DisplayVersion);
            Note(c, y, "非官方本地管理工具，与 Anthropic 无关联。\n不向 Claude / Anthropic 发请求，不读取凭据，不修改官方应用。");
            y = SwitchRow(c, y + 54, "允许检查 GitHub 更新", "默认关闭。开启后每次打开启动器检查一次，也可手动检查；只访问本项目 GitHub 下载地址。", () => draft.AllowUpdates, v => draft.AllowUpdates = v);
            update = ActionRow(c, 0, y + 6, "检查启动器更新…", async () => await CheckUpdate(true));
            ActionRow(c, 0, y + 50, "查看本地使用帮助", ShowHelp);
            ActionRow(c, 0, y + 94, "复制基础诊断信息", CopyDiagnostics);
            Note(c, y + 144, "诊断仅包含版本、Windows 版本和联网开关，不包含账号、路径、日志或凭据。");

            // 底部：反馈、恢复默认（更改即时保存，无保存／撤销按钮）
            settingsFeedback = At(page, new TextBlock { Text = "更改即时保存并生效", FontSize = 12, Foreground = B(Muted), TextTrimming = TextTrimming.CharacterEllipsis }, 230, 656, 390);
            AutomationProperties.SetAutomationId(settingsFeedback, "SettingsFeedback");
            var reset = At(page, Action("恢复默认…", "Btn", null, 14), 646, 646, 110, 38);
            AutomationProperties.SetAutomationId(reset, "ResetSettings");
            reset.Click += delegate {
                if (Ask("恢复启动器默认设置？", "立即保存并生效。不会移除账号、登录保护、开机启动项或任何 Claude 数据。", new[] { "取消", "恢复默认" }, 0, 1) == 1) ResetSettings();
            };
            ShowSettingsSection(0);
            SyncSettings();
        }
        double Heading(Canvas c, string text) {
            At(c, T(text, 17, Ink, FontWeights.SemiBold), 0, 0, 518);
            return 44;
        }
        void Note(Canvas c, double y, string text) {
            At(c, new TextBlock { Text = text, FontSize = 12, Foreground = B(Muted), TextWrapping = TextWrapping.Wrap, LineHeight = 19 }, 0, y, 518);
        }
        // 开关行：整行可点（标题 + 说明 + 开关）；更改即时保存。
        double SwitchRow(Canvas c, double y, string title, string detail, Func<bool> get, Action<bool> set) {
            var text = new StackPanel { Width = 450, HorizontalAlignment = HorizontalAlignment.Left };
            text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = B(Ink) });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = B(Muted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            var box = At(c, new CheckBox { Style = (Style)window.FindResource("Switch"), Content = text }, 0, y, 518);
            AutomationProperties.SetName(box, title);
            box.Click += delegate { set(box.IsChecked == true); DraftChanged(); };
            settingsSync.Add(() => box.IsChecked = get());
            return y + 72;
        }
        double ChoiceRow(Canvas c, double y, string title, string[] labels, object[] values, Func<object> get, Action<object> set, Func<bool> enabled) {
            At(c, T(title, 14, Ink, FontWeights.SemiBold), 0, y + 6, 260);
            var combo = At(c, new ComboBox { ItemsSource = labels, FontSize = 13 }, 290, y, 228, 32);
            AutomationProperties.SetName(combo, title);
            combo.SelectionChanged += delegate {
                if (syncingSettings || combo.SelectedIndex < 0) return;
                set(values[combo.SelectedIndex]); DraftChanged();
            };
            settingsSync.Add(() => {
                combo.SelectedIndex = Math.Max(0, Array.IndexOf(values, get()));
                combo.IsEnabled = enabled == null || enabled();
            });
            return y + 48;
        }
        Button ActionRow(Canvas c, double x, double y, string text, Action handler) {
            var button = At(c, Action(text, "Btn", null, 13), x, y, 250, 34);
            button.Click += delegate { handler(); };
            return button;
        }
        void ShowSettingsSection(int index) {
            settingsSection = index;
            for (int i = 0; i < settingsSections.Length; i++) {
                bool on = i == index;
                settingsSections[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                settingsTabs[i].Background = on ? B("#EDDDC9") : Brushes.Transparent;
                settingsTabs[i].Foreground = B(on ? "#9A6447" : "#3A332D");
                foreach (TextBlock label in ((StackPanel)settingsTabs[i].Content).Children) { label.Foreground = settingsTabs[i].Foreground; label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal; }
            }
            if (index == 1) autoStartStatus.Text = AutoStartStatus();
        }
        // 控件从编辑副本重新取值。
        void SyncSettings() {
            syncingSettings = true;
            try { foreach (Action sync in settingsSync) sync(); } finally { syncingSettings = false; }
        }
        // 每次更改即时保存；被拒绝或写入失败时控件回到已保存值。
        void DraftChanged() {
            if (!SaveSettings()) { draft = settings.Clone(); SyncSettings(); }
        }
        internal void ResetSettings() { draft = new LauncherSettings(); DraftChanged(); }
        internal bool SaveSettings() {
            LauncherSettings next = draft.Clone();
            if (updating && next.AllowUpdates != settings.AllowUpdates) { Alert("更新正在进行", "请等待当前更新结束后再改变联网选项。"); return false; }
            try { next.Save(settingsPath); }
            catch (Exception error) { Diagnostics.Error("settings_save_error", error); Alert("保存设置失败", error.Message + "\n运行设置保持原样。"); return false; }
            settings = next; draft = next.Clone();
            ApplySettings(); SyncSettings();
            settingsFeedback.Text = "设置已保存并生效，仅作用于本地启动器";
            Diagnostics.Write("settings_saved", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(next.Values()));
            AddLog("本地设置已保存");
            return true;
        }
        // 把已保存设置应用到启动器自身：刷新间隔、托盘、插画、提示、日志上限、上次页面记录。不触碰任何 Claude。
        void ApplySettings() {
            timer.Interval = TimeSpan.FromSeconds(settings.RefreshInterval);
            if (tray != null) tray.Visible = settings.ShowTray;
            foreach (UIElement element in artwork) element.Visibility = settings.ShowArtwork ? Visibility.Visible : Visibility.Collapsed;
            if (!settings.LocalNotifications) { toastTimer.Stop(); toast.Visibility = Visibility.Collapsed; }
            if (logs.Count > settings.LogLimit) { logs.RemoveRange(settings.LogLimit, logs.Count - settings.LogLimit); RenderLogs(); }
            if (settings.RememberPage) RememberPage();
            else try { if (File.Exists(lastPagePath)) File.Delete(lastPagePath); } catch (Exception error) { Diagnostics.Error("last_page_clear_error", error); }
        }
        void RememberPage() {
            if (!settings.RememberPage || snapshotMode) return;
            try { LauncherSettings.WriteLastPage(lastPagePath, LauncherSettings.Pages[page]); } catch (Exception error) { Diagnostics.Error("last_page_write_error", error); }
        }
        internal string AutoStartStatus() {
            string exe = Assembly.GetExecutingAssembly().Location, value;
            try { value = AutoStart.Current(); } catch (Exception error) { return "无法读取开机启动状态：" + error.Message; }
            if (value == null) return AutoStart.IsInstalledCopy(exe, AutoStart.InstalledPath) ? "开机启动未开启。此项单独保存，确认后立即生效，不进入草稿。" : "开机启动未开启。只有安装到默认位置的正式启动器可以开启（当前为开发副本或其他位置）。";
            return AutoStart.PointsTo(value, exe) ? "开机启动已开启：登录 Windows 后只打开启动器，不启动 Claude。" : "开机启动项指向其他位置，启动器不会修改：" + value;
        }
        void ManageAutoStart() {
            string exe = Assembly.GetExecutingAssembly().Location, value;
            try { value = AutoStart.Current(); } catch (Exception error) { Alert("无法读取开机启动状态", error.Message); return; }
            bool enabled = AutoStart.PointsTo(value, exe);
            if (!enabled && value != null) { Alert("开机启动项指向其他位置", "当前值：" + value + "\n启动器不会修改不属于本程序的值。"); return; }
            if (!enabled && !AutoStart.IsInstalledCopy(exe, AutoStart.InstalledPath)) {
                Alert("请使用已安装的启动器", "开机启动只为安装到默认位置的正式副本开启，避免登录时运行临时开发版本：\n" + Diagnostics.Redact(AutoStart.InstalledPath)); return;
            }
            if (Ask(enabled ? "关闭开机启动？" : "开启开机启动？", "只更改本启动器在当前 Windows 用户中的开机启动项，立即生效。不会自动启动任何 Claude 账号。",
                new[] { "取消", enabled ? "关闭开机启动" : "开启开机启动" }, 0, 1) != 1) return;
            try {
                if (enabled) AutoStart.Disable(exe); else AutoStart.Enable(exe);
                Diagnostics.Write("autostart_changed", "enabled=" + !enabled);
                AddLog(enabled ? "已关闭开机启动" : "已开启开机启动");
            } catch (Exception error) { Diagnostics.Error("autostart_error", error); Alert("开机启动设置失败", error.Message); }
            autoStartStatus.Text = AutoStartStatus(); settingsFeedback.Text = autoStartStatus.Text;
        }
        void ExportSettings() {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "启动器设置.json", Filter = "JSON 文件|*.json" };
            if (dialog.ShowDialog(window) != true) return;
            try { File.WriteAllText(dialog.FileName, settings.ExportJson(), new UTF8Encoding(false)); settingsFeedback.Text = "已导出保存的设置，不含账号或凭据"; }
            catch (Exception error) { Alert("导出失败", error.Message); }
        }
        void ShowHelp() {
            Alert("本地使用帮助", "实例管理：手动启动或打开指定账号；“···”菜单可显示或复制目录、管理登录保护、删除账号（移到回收站）。\n账号管理：查看本机账号空间与运行状态。\n日志记录：本次运行的提示，可导出或清空，仅保留在内存。\n系统设置：切换或选择即保存生效；恢复默认确认后立即保存，不处理账号数据。\n\n启动器不查询 Claude 服务、不读取凭据，不控制官方客户端自身正常联网。联网更新默认关闭，开启后只访问本项目 GitHub。");
        }
        internal string DiagnosticsText() {
            return "Claude MultiLauncher " + Updates.DisplayVersion + " (" + Updates.Version + ")\nWindows " + Environment.OSVersion.Version +
                "\nNetwork: GitHub updates " + (settings.AllowUpdates ? "enabled" : "disabled") + "\nClaude/Anthropic requests: prohibited";
        }
        void CopyDiagnostics() {
            try { Clipboard.SetText(DiagnosticsText()); settingsFeedback.Text = "基础诊断已复制，不含账号、路径或日志"; }
            catch (Exception error) { Alert("无法复制诊断信息", error.Message); }
        }

        void ShowPage(int index) {
            page = index;
            for (int i = 0; i < 4; i++) {
                pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
                nav[i].Background = i == index ? B("#EDDDC9") : Brushes.Transparent;
            }
            Canvas.SetTop(indicator, 188 + index * 56);
            Render();
        }
        // 仅用于测试与离屏预览：以给定进程状态渲染某页，不扫描真实进程。
        bool snapshotMode;
        internal FrameworkElement Snapshot(int index, List<Instance> state) {
            snapshotMode = true; healthy = true; instances = state; ShowPage(index);
            return (FrameworkElement)((ScrollViewer)window.Content).Content;
        }

        IEnumerable<int> Busy() { return pending.Keys.Concat(checking).ToList(); }
        bool IsBusy(int number) { return pending.ContainsKey(number) || checking.Contains(number); }

        void Message(string text, bool error) {
            message.Text = text; message.Foreground = B(error ? "#C9604A" : Muted);
            if (!String.IsNullOrEmpty(text) && text != lastLogged) { lastLogged = text; AddLog(text); if (error) Notify(text); }
        }
        // SPEC 消息通知：按已保存设置显示窗口内提示条，可选系统提示音；不推送、不读取 Claude 消息。
        internal bool Notify(string text) {
            if (!settings.LocalNotifications) return false;
            toastText.Text = text; toast.Visibility = Visibility.Visible;
            toastTimer.Stop(); toastTimer.Start();
            if (settings.NotificationSound && !snapshotMode) System.Media.SystemSounds.Exclamation.Play();
            return true;
        }
        void AddLog(string text) {
            string level = new[] { "失败", "错误", "未通过", "无法" }.Any(text.Contains) ? "错误" : new[] { "超时", "保护", "未确认", "已变化", "多个" }.Any(text.Contains) ? "警告" : "信息";
            Match match = Regex.Match(text, "账号 ([0-9]{2})");
            DateTime now = DateTime.Now;
            logs.Insert(0, new LogEntry { At = now, Time = now.ToString("yyyy-MM-dd HH:mm:ss"), Level = level, Instance = match.Success ? "Claude " + Int32.Parse(match.Groups[1].Value) : "系统", Text = text });
            if (logs.Count > settings.LogLimit) logs.RemoveRange(settings.LogLimit, logs.Count - settings.LogLimit);
            RenderLogs();
        }
        // SPEC 账号与日志交互：时间、实例、级别、全文搜索组合筛选；结果保持新到旧。
        internal static List<LogEntry> FilterLogs(IEnumerable<LogEntry> entries, string query, string instance, string level, DateTime? since) {
            string q = (query ?? "").Trim();
            return entries.Where(e => (instance == null || e.Instance == instance) && (level == null || e.Level == level) && (since == null || e.At >= since.Value) &&
                (q.Length == 0 || (e.Time + " " + e.Level + " " + e.Instance + " " + e.Text).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
        }
        List<LogEntry> FilteredLogs() {
            if (logTime == null) return logs.ToList();
            DateTime? since = logTime.SelectedIndex == 1 ? DateTime.Now.AddMinutes(-5) : logTime.SelectedIndex == 2 ? DateTime.Now.AddHours(-1) : (DateTime?)null;
            string instance = logInstance.SelectedIndex <= 0 ? null : (string)logInstance.SelectedItem;
            string level = logLevel.SelectedIndex <= 0 ? null : (string)logLevel.SelectedItem;
            return FilterLogs(logs, logSearch.Text, instance, level, since);
        }
        void RenderLogs() {
            if (logRows == null || logPrev == null) return;
            logRows.Children.Clear();
            List<LogEntry> shown = FilteredLogs();
            int pages = Math.Max(1, (shown.Count + 9) / 10);
            logPageIndex = Math.Max(0, Math.Min(logPageIndex, pages - 1));
            if (shown.Count == 0) {
                var empty = T(logs.Count == 0 ? "暂无日志" : "没有符合条件的日志，可调整筛选或搜索", 13, "#8A7F74", FontWeights.Normal); empty.TextAlignment = TextAlignment.Center;
                At(logRows, empty, 0, 190, 736);
            }
            var slice = shown.Skip(logPageIndex * 10).Take(10).ToList();
            for (int i = 0; i < slice.Count; i++) {
                LogEntry entry = slice[i]; double y = i * 40;
                if (i > 0) At(logRows, new Rectangle { Fill = B("#F3ECE3") }, 1, y, 734, 1);
                At(logRows, T(entry.Time, 13, "#8A7F74", FontWeights.Normal), 24, y + 10, 166);
                string[] tone = entry.Level == "错误" ? new[] { "#FBE3DF", "#C9604A" } : entry.Level == "警告" ? new[] { "#FBEFD9", "#B7832A" } : new[] { "#E3F3EA", "#3C8A68" };
                var pill = At(logRows, new Border { CornerRadius = new CornerRadius(6), Background = B(tone[0]) }, 196, y + 9, 44, 22);
                pill.Child = new TextBlock { Text = entry.Level, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B(tone[1]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                At(logRows, T(entry.Instance, 13, Ink, FontWeights.Normal), 290, y + 10, 100);
                // 只读可选择的事件内容；截断时悬停显示全文。
                var text = At(logRows, new TextBox { Text = entry.Text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = B(Ink), FontSize = 13, Padding = new Thickness(0), ToolTip = entry.Text }, 402, y + 9, 314);
                AutomationProperties.SetName(text, "日志内容");
            }
            logPrev.IsEnabled = logPageIndex > 0;
            logNext.IsEnabled = logPageIndex < pages - 1;
            logPageLabel.Text = "第 " + (logPageIndex + 1) + " / " + pages + " 页";
            logSummary.Text = (shown.Count == logs.Count ? "共 " + logs.Count + " 条" : "筛选出 " + shown.Count + " / " + logs.Count + " 条") + " · 每页 10 条";
        }
        // 导出全部筛选结果（不只是当前页），按时间正序。
        void ExportLogs() {
            List<LogEntry> shown = FilteredLogs();
            if (shown.Count == 0) { Alert("没有可导出的日志", "当前筛选条件下没有日志。"); return; }
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "Claude 启动器日志.txt", Filter = "文本文件|*.txt" };
            if (dialog.ShowDialog(window) != true) return;
            try { File.WriteAllText(dialog.FileName, LogText(shown), new UTF8Encoding(true)); Message("已导出 " + shown.Count + " 条日志。", false); }
            catch (Exception error) { Alert("导出失败", error.Message); }
        }
        internal static string LogText(IEnumerable<LogEntry> newestFirst) {
            var output = new StringBuilder();
            foreach (LogEntry entry in newestFirst.Reverse()) output.Append(entry.Time + "\t" + entry.Level + "\t" + entry.Instance + "\t" + entry.Text + "\r\n");
            return output.ToString();
        }

        // ---- 账号页：别名、筛选、选择 ----
        int accountsShown;
        string DisplayName(int n) {
            string alias;
            return accountNames.TryGetValue(n, out alias) ? alias + " · " + Two(n) : "账号 " + Two(n);
        }
        // 当前可见且选中的账号；隐藏的选择在此清除，避免误操作。
        void UpdateSelection() {
            if (selectAll == null) return;
            var visible = Enumerable.Range(1, 10).Where(n => rows[n].Root.Visibility == Visibility.Visible).ToList();
            selectedAccounts.RemoveWhere(n => !visible.Contains(n));
            foreach (int n in Enumerable.Range(1, 10)) rows[n].Select.IsChecked = selectedAccounts.Contains(n);
            selectAll.IsChecked = selectedAccounts.Count == 0 ? false : selectedAccounts.Count == visible.Count ? true : (bool?)null;
            selectAll.IsEnabled = visible.Count > 0;
            copySelected.IsEnabled = selectedAccounts.Count > 0;
            tableFooter.Text = "共 " + accountsShown + " 个账号" + (visible.Count != accountsShown ? " · 显示 " + visible.Count + " 个" : "") + " · 选中 " + selectedAccounts.Count + " 项";
        }
        void ToggleSelectAll() {
            var visible = Enumerable.Range(1, 10).Where(n => rows[n].Root.Visibility == Visibility.Visible).ToList();
            bool all = visible.Count > 0 && visible.All(selectedAccounts.Contains);
            selectedAccounts.Clear();
            if (!all) foreach (int n in visible) selectedAccounts.Add(n);
            UpdateSelection();
        }
        internal string SelectedPathsText() {
            return String.Join("\r\n", selectedAccounts.OrderBy(n => n).Where(n => rows[n].Root.Visibility == Visibility.Visible).Select(n => Core.DisplayFolder(n)));
        }
        void CopySelectedPaths() {
            UpdateSelection();
            if (selectedAccounts.Count == 0) return;
            try { Clipboard.SetText(SelectedPathsText()); Message("已复制 " + selectedAccounts.Count + " 个账号的目录路径。", false); }
            catch (Exception error) { Message("无法复制路径：" + error.Message, true); }
        }
        void EditAlias(int n) {
            if (namesError != null) { Alert("无法编辑别名", "别名文件无法读取，为避免覆盖已停止写入：\n" + namesError); return; }
            string current; accountNames.TryGetValue(n, out current);
            string input = AskText("设置账号 " + Two(n) + " 的本地别名", "最多 24 个字，只保存在本机启动器中，不改变邮箱、身份或目录。留空恢复为“账号 " + Two(n) + "”。", current ?? "");
            if (input == null) return;
            string error = SetAlias(n, input);
            if (error != null) Alert("别名未保存", error);
        }
        // 返回 null 表示成功；失败时返回原因并保持原别名。
        internal string SetAlias(int n, string input) {
            if (namesError != null) return "别名文件无法读取，已停止写入。";
            string name = AccountNames.Validate(input);
            if (name == null) return "别名最多 24 个字，不能包含换行或控制字符。";
            var next = new Dictionary<int, string>(accountNames);
            if (name.Length == 0) next.Remove(n); else next[n] = name;
            try { AccountNames.Save(namesPath, next); }
            catch (Exception error) { Diagnostics.Error("account_name_save_error", error); return error.Message; }
            accountNames = next;
            cards[n].Title.Text = rows[n].Name.Text = DisplayName(n);
            rows[n].Name.ToolTip = DisplayName(n);
            Render();
            Message(name.Length == 0 ? "账号 " + Two(n) + " 已恢复默认名称。" : "账号 " + Two(n) + " 的本地别名已设为“" + name + "”。", false);
            return null;
        }
        string AskText(string title, string detail, string value) {
            var dialog = Dialog(title);
            if (window.IsLoaded && window.IsVisible) dialog.Owner = window; else { dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen; dialog.Topmost = true; }
            var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), Foreground = B("#4A433C"), LineHeight = 21 });
            var box = new TextBox { Text = value, MaxLength = AccountNames.MaxLength, FontSize = 14, Padding = new Thickness(8, 6, 8, 6), BorderBrush = B("#E3D9CD"), Margin = new Thickness(0, 0, 0, 20) };
            AutomationProperties.SetName(box, title);
            panel.Children.Add(box);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", Style = (Style)window.FindResource("Btn"), Padding = new Thickness(16, 7, 16, 7), IsCancel = true };
            var ok = new Button { Content = "保存", Style = (Style)window.FindResource("Primary"), Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
            string result = null;
            ok.Click += delegate { result = box.Text; dialog.DialogResult = true; };
            actions.Children.Add(cancel); actions.Children.Add(ok); panel.Children.Add(actions);
            dialog.Loaded += delegate { box.Focus(); box.SelectAll(); };
            dialog.ShowDialog();
            return result;
        }
        TextBox SearchBox(Canvas canvas, double x, double y, double width, double height, string placeholder, string name) {
            var grid = new Grid { Margin = new Thickness(14, 0, 12, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var glyph = Glyph("", 13, Muted); glyph.Margin = new Thickness(0, 0, 10, 0); grid.Children.Add(glyph);
            var hint = T(placeholder, 13, Faint, FontWeights.Normal); hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false;
            Grid.SetColumn(hint, 1); grid.Children.Add(hint);
            var box = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = B(Ink), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
            Grid.SetColumn(box, 1); grid.Children.Add(box);
            box.TextChanged += delegate { hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            AutomationProperties.SetName(box, name);
            var border = Panel(10, "#EBFFFDFA"); border.Child = grid;
            At(canvas, border, x, y, width, height);
            return box;
        }
        ComboBox Choice(Canvas canvas, double x, double y, double width, double height, string[] items, string name) {
            var combo = At(canvas, new ComboBox { ItemsSource = items, SelectedIndex = 0, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 0, 8, 0) }, x, y, width, height);
            AutomationProperties.SetName(combo, name);
            return combo;
        }

        void Render() {
            if (cards.Count < 10) return;
            var busy = Busy();
            int shown = 0, running = 0, attention = 0;
            for (int n = 1; n <= 10; n++) {
                var found = instances.Where(x => x.Number == n).ToList();
                bool hasData = Core.HasData(n), isChecking = checking.Contains(n), starting = pending.ContainsKey(n), incomplete = incompleteDeletes.Contains(n);
                bool visible = incomplete || Core.Visible(n, instances, busy, x => x == n && hasData);
                bool broken = !healthy || errors.ContainsKey(n) || incomplete;
                string state = !healthy ? "状态读取失败" : incomplete ? "删除未完成 · 请重试删除" : isChecking ? "正在检查…" : starting ? "正在启动…" :
                    found.Count > 1 ? "运行中 · 多个实例，请检查" : found.Count == 1 ? "运行中" : errors.ContainsKey(n) ? "未运行 · 操作失败" : "未运行";
                string dot = broken ? "#D0533F" : starting || isChecking ? "#E0A23A" : found.Count > 0 ? "#3BAA6E" : "#B9B1A7";
                Card card = cards[n];
                card.Root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                card.Status.Text = state; card.Status.Foreground = B(broken ? "#C9604A" : Muted);
                card.Status.ToolTip = errors.ContainsKey(n) ? errors[n] : found.Count > 0 ? "主进程 PID：" + String.Join("、", found.Select(x => x.ProcessId)) : Core.DisplayFolder(n);
                card.Dot.Fill = B(dot);
                LaunchRecord record; launched.TryGetValue(n, out record);
                card.Code.Text = healthy ? Core.CodeState(n, instances, record) : "Code 未确认";
                ((FrameworkElement)card.Code.Parent).ToolTip = n == 1 ? "01 沿用默认 Code 配置，与普通终端 CLI 共用。" : card.Code.Text == "Code 已指定" ? "本次启动时已指定独立 Code 配置；尚未验证 Code 的实际写入位置。" :
                    card.Code.Text == "Code 待启动" ? "从启动器启动后才会指定独立 Code 配置。" : "运行中实例的 Code 配置未确认。保存工作后正常退出该 Claude，再从这里启动；不会迁移或删除旧记录。";
                string title = incomplete ? "重试删除" : isChecking ? "检查中" : starting ? "启动中" : found.Count > 0 ? "打开" : hasData ? "启动" : "创建";
                card.Action.Content = title;
                card.Action.Style = (Style)window.FindResource(title == "打开" ? "Primary" : title == "创建" ? "Create" : "Btn");
                card.Action.IsEnabled = healthy && !deleting && !updating && (incomplete || (!starting && !isChecking && (loginAccount == 0 || loginAccount == n)));
                AutomationProperties.SetName(card.Action, "账号 " + Two(n) + "，" + state);

                Row row = rows[n];
                if (!visible) { row.Root.Visibility = Visibility.Collapsed; continue; }
                shown++;
                if (found.Count > 0) running++;
                if (broken && healthy) attention++;
                bool rowBusy = starting || isChecking;
                // 进程扫描失败时不伪装成确定状态（SPEC 账号与日志交互）。
                string category = !healthy ? "未知" : broken ? "异常" : rowBusy ? "处理中" : found.Count > 0 ? "运行中" : "未运行";
                string alias; accountNames.TryGetValue(n, out alias);
                bool matches = AccountNames.Matches(n, alias, Core.DisplayFolder(n), accountSearch.Text) &&
                    (accountStatus.SelectedIndex <= 0 || StatusFilters[accountStatus.SelectedIndex] == category);
                row.Root.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                row.Status.Text = category;
                row.Status.Foreground = B(category == "异常" ? "#C9604A" : category == "处理中" ? "#B7832A" : category == "运行中" ? "#3C8A68" : "#8A7F74");
                row.Status.ToolTip = card.Status.ToolTip;
                row.Dot.Fill = B(dot);
                row.Recent.Text = Recent(n);
            }
            emptyHint.Visibility = shown == 0 && healthy ? Visibility.Visible : Visibility.Collapsed;
            summary.Text = healthy ? "本机 " + shown + " 个账号 · " + running + " 个运行中" : "状态暂不可用";
            create.IsEnabled = healthy && loginAccount == 0 && !deleting && !updating;
            endLogin.Visibility = loginAccount != 0 ? Visibility.Visible : Visibility.Collapsed;
            endLogin.IsEnabled = checking.Count == 0 && pending.Count == 0;
            int[] values = { shown, running, attention };
            for (int i = 0; i < 3; i++) stats[i].Text = values[i].ToString("00");
            accountsEmpty.Visibility = shown > 0 && !rows.Values.Any(r => r.Root.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
            accountsShown = shown;
            UpdateSelection();
            if (healthy && loginAccount != 0)
                Message("账号 " + Two(loginAccount) + " 登录保护中。" + (instances.Any(x => x.Number != loginAccount) ? "检测到其他 Claude！请停止登录，并正常退出其他实例。" : "完成登录并核对身份后，点「结束登录保护」恢复多开。"), false);
        }
        // 目录修改时间只是元数据，近似“最近使用”，不打开账号文件。
        static string Recent(int number) {
            try {
                string folder = Core.DisplayFolder(number);
                if (!Directory.Exists(folder)) return "—";
                DateTime modified = Directory.GetLastWriteTime(folder);
                if (modified.Date == DateTime.Today) return "今天 " + modified.ToString("HH:mm");
                if (modified.Date == DateTime.Today.AddDays(-1)) return "昨天 " + modified.ToString("HH:mm");
                return modified.Month + " 月 " + modified.Day + " 日";
            } catch (IOException) { return "—"; } catch (UnauthorizedAccessException) { return "—"; }
        }

        async Task Refresh() {
            if (scanning || closed) return;
            scanning = true;
            try { await ScanNow(); } finally { scanning = false; }
        }
        // 重新扫描进程并更新状态；返回扫描是否可信。扫描失败时全局暂停启动。
        async Task<bool> ScanNow() {
            try {
                var latest = await Task.Run(() => Core.Scan());
                string found = executable;
                if (String.IsNullOrEmpty(found) || !File.Exists(found)) found = await Task.Run(() => Core.FindExecutable(latest));
                if (closed) return false;
                Apply(latest); executable = found;
                lastRefreshError = ""; scanError = null;
                if (!healthy) Message("选择一个账号。已运行的账号会直接切回原窗口。", false);
                healthy = true;
            } catch (Exception error) {
                if (closed) return false;
                if (lastRefreshError != error.Message) { Diagnostics.Error("refresh_error", error); lastRefreshError = error.Message; }
                healthy = false; scanError = error.Message; Message(error.Message, true);
            }
            if (!closed) Render();
            return healthy;
        }
        void Apply(List<Instance> latest) {
            processWatch.Refresh(latest.Where(x => x.Number > 0));
            if (DateTime.UtcNow >= nextHeartbeat) { Diagnostics.Write("heartbeat", "accounts=" + latest.Count(x => x.Number > 0)); nextHeartbeat = DateTime.UtcNow.AddSeconds(30); }
            string processState = String.Join(",", latest.OrderBy(x => x.Number).ThenBy(x => x.ProcessId).Select(x => x.Number + ":" + x.ProcessId));
            if (processState != lastProcessState) {
                foreach (var gone in instances.Where(x => !latest.Any(y => y.ProcessId == x.ProcessId))) Diagnostics.Write("account_process_gone", "slot=" + gone.Number + "; pid=" + gone.ProcessId + "; reason=unknown (可能正常退出，不能据此判定崩溃)");
                Diagnostics.Write("account_process_state", processState); lastProcessState = processState;
            }
            instances = latest;
            // 记录的进程退出、PID 变化或出现重复主进程后，不再沿用“已指定”。
            foreach (int number in launched.Keys.ToArray()) {
                var found = latest.Where(x => x.Number == number).ToList();
                bool alive = found.Count == 1 && found[0].ProcessId == launched[number].ProcessId;
                if (!alive && !pending.ContainsKey(number)) launched.Remove(number);
            }
            foreach (int number in pending.Keys.ToArray()) {
                IntPtr handle = instances.Where(x => x.Number == number).Select(x => Native.WindowFor(x.ProcessId)).FirstOrDefault(x => x != IntPtr.Zero);
                if (handle != IntPtr.Zero) {
                    pending.Remove(number);
                    // 用户已切到其他应用时，不再抢占焦点。
                    if (window.IsActive) Native.Focus(handle);
                    Message("账号 " + Two(number) + " 已打开。首次使用请在 Claude 中登录。", false);
                } else if (DateTime.UtcNow - pending[number] > TimeSpan.FromSeconds(35)) {
                    pending.Remove(number);
                    errors[number] = Core.Running(number, instances) ? "已在运行但尚未显示窗口；可能在托盘中，请稍后重试。" : "启动失败：35 秒内未出现 Claude 主进程。请重试。";
                    Message("账号 " + Two(number) + "：" + errors[number], true);
                }
            }
        }

        async Task NewInstance() {
            if (!await ScanNow() || loginAccount != 0) return;
            int number = Core.NextFree(instances, Busy(), Core.HasData);
            if (number == 0) { Alert("10 个账号空间都已创建", "请直接在列表中启动或打开对应账号。"); return; }
            await Select(number);
        }
        async Task Select(int number) {
            if (IsBusy(number) || deleting || updating || incompleteDeletes.Contains(number)) return;
            Diagnostics.Write("account_click", "slot=" + number);
            if (!await ScanNow()) { Alert("暂时不能打开账号", scanError); return; }
            string failure = Core.OpeningBlocker(number, 0, instances, loginAccount, Busy());
            if (failure != null) { Alert("暂时不能打开账号", failure); return; }
            bool create = loginAccount == 0 && !Core.Running(number, instances) && !Core.HasData(number);
            if (create) {
                checking.Add(number); Render(); // 对话框期间占用该编号，防止重入。
                int choice = Ask("创建账号 " + Two(number) + "？",
                    "将创建独立的本地数据目录。\n\n直接创建：立即启动，其他 Claude 保持运行。登录时浏览器回调可能打开其他窗口，登录后请核对身份。\n\n进入登录保护：需先正常退出其他 Claude；保护期间只允许打开这个账号，登录更稳妥。",
                    new[] { "直接创建", "进入登录保护", "取消" }, 2, 0);
                checking.Remove(number);
                bool ok = await ScanNow();
                if (choice == 1) { await ProtectAndOpen(number); return; }
                if (choice != 0) return;
                failure = ok ? Core.OpeningBlocker(number, 0, instances, loginAccount, Busy()) : scanError;
                if (failure != null || Core.Running(number, instances)) { Alert("状态已变化，未创建", failure ?? "该账号已在运行，请重新点击。"); return; }
            }
            Instance current = instances.FirstOrDefault(x => x.Number == number);
            await CheckAndOpen(number, current == null ? 0 : current.ProcessId, create);
        }
        // 递归存储检查在后台进行；完成后重新扫描，核对 PID 与登录保护状态再操作。
        async Task CheckAndOpen(int number, int expectedPid, bool create) {
            if (IsBusy(number)) return;
            checking.Add(number); errors.Remove(number); Render();
            Message("正在检查账号 " + Two(number) + " 的存储…", false);
            string failure = null;
            try { await Task.Run(() => Storage.Validate(number, Core.Root, ProfileDeletion.Local)); }
            catch (Exception error) { failure = error.Message; Diagnostics.Error("storage_check_error", error); }
            finally { checking.Remove(number); }
            if (closed) return;
            if (failure != null) {
                errors[number] = failure; Message("账号 " + Two(number) + "：" + failure, true);
                Alert("账号存储隔离检查未通过", failure); return;
            }
            if (!await ScanNow()) { Alert("实例或保护状态已变化", scanError); return; }
            failure = Core.OpeningBlocker(number, expectedPid, instances, loginAccount, Busy());
            if (failure != null) { Message("账号 " + Two(number) + "：" + failure, true); Alert("实例或保护状态已变化", failure); return; }
            Instance running = instances.FirstOrDefault(x => x.Number == number);
            if (running != null) { await Activate(number, running); return; }
            if (!create && loginAccount == 0 && !Core.HasData(number)) { Message("账号目录已变化，未创建新目录。请重新点击。", true); return; }
            if (!await ConfirmEnvironment(number)) return;
            // 对话框期间状态可能变化：重新扫描并核对后再启动。
            if (!await ScanNow()) { Alert("实例或保护状态已变化", scanError); return; }
            failure = Core.OpeningBlocker(number, 0, instances, loginAccount, Busy());
            if (failure != null || Core.Running(number, instances)) { Alert("状态已变化，未启动", failure ?? "该账号已在运行，请重新点击。"); return; }
            if (!create && loginAccount == 0 && !Core.HasData(number)) { Message("账号目录已变化，未创建新目录。请重新点击。", true); return; }
            await Launch(number);
        }
        // 每次启动新实例前做本机代理检测，用户确认后才启动；不访问外部网络。
        async Task<bool> ConfirmEnvironment(int number) {
            checking.Add(number); Render();
            Message("账号 " + Two(number) + "：正在检测电脑网络环境…", false);
            List<string> lines = null; bool ok = false;
            try { ok = await Task.Run(() => ProxyCheck.Report(out lines)); }
            catch (Exception error) { Diagnostics.Error("proxy_check_error", error); lines = new List<string> { "✗ 检测失败：" + error.Message }; }
            if (closed) { checking.Remove(number); return false; }
            int choice = Ask(ok ? "网络环境检测：已检测到代理" : "网络环境检测：未检测到可用代理",
                string.Join("\n", lines) + "\n\n" + (ok ? "请确认代理节点可用后继续启动。" : "Claude 需要可用的代理才能正常连接。建议先开启代理软件的系统代理或 TUN 模式，再继续启动。")
                + "\n\n仅读取本机系统代理设置与网卡，不访问外部网络，无法确认出口 IP 所在地区。",
                new[] { "确认启动", "取消" }, 1, 0);
            checking.Remove(number); Render();
            if (choice != 0) Message("账号 " + Two(number) + "：已取消启动。", false);
            return choice == 0;
        }
        async Task Launch(int number) {
            pending[number] = DateTime.UtcNow; Render();
            Message("正在启动账号 " + Two(number) + "…", false);
            try {
                string path = executable;
                var requested = DateTime.UtcNow;
                int pid = await Task.Run(() => Core.Launch(number, path));
                if (number != 1) launched[number] = new LaunchRecord { ProcessId = pid, Requested = requested };
            } catch (Exception error) {
                Diagnostics.Error("account_open_error", error);
                pending.Remove(number); errors[number] = error.Message;
                Message("账号 " + Two(number) + " 启动失败：" + error.Message, true);
            }
            if (!closed) Render();
        }
        async Task Activate(int number, Instance running) {
            IntPtr handle = Native.WindowFor(running.ProcessId);
            if (handle != IntPtr.Zero) {
                // SPEC 启动行为：关闭“点击账号时恢复最小化窗口”时，不还原、不切换已最小化的窗口。
                if (!settings.RestoreMinimized && Native.IsMinimized(handle)) {
                    Message("账号 " + Two(number) + " 的窗口已最小化；按设置未自动恢复，请从任务栏打开。", false);
                    return;
                }
                bool focused = Native.Focus(handle);
                Diagnostics.Write("account_reuse", "slot=" + number + "; pid=" + running.ProcessId + "; focused=" + focused);
                Message(focused ? "已切回账号 " + Two(number) + "（PID " + running.ProcessId + "），没有重复启动。" : "账号 " + Two(number) + " 已经运行；Windows 未允许切换焦点，请从任务栏打开。", !focused);
                return;
            }
            // SPEC Windows 例外：托盘实例无可见窗口时，用该 PID 自身的程序同目录再启动，由 Claude 单实例锁转交窗口。
            pending[number] = DateTime.UtcNow; Render();
            try {
                Diagnostics.Write("account_tray_relaunch", "slot=" + number + "; pid=" + running.ProcessId + "; claude=" + running.Executable);
                await Task.Run(() => Core.Launch(number, running.Executable));
                // shortcut: 固定等 5 秒让转交进程退出后复查一次；若出现慢退出的误报，再改为多次复查。
                await Task.Delay(TimeSpan.FromSeconds(5));
                var after = await Task.Run(() => Core.Scan());
                if (closed) return;
                Apply(after);
                Core.Single(number, after);
            } catch (Exception error) {
                Diagnostics.Error("account_open_error", error);
                pending.Remove(number); errors[number] = error.Message;
                Message(error.Message, true);
            }
            if (!closed) Render();
        }

        void ShowMore(int number, Button anchor) {
            bool exists = Core.HasData(number);
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var reveal = new MenuItem { Header = "在资源管理器中显示目录", IsEnabled = exists };
            reveal.Click += delegate { OpenFolder(Core.DisplayFolder(number)); };
            var copy = new MenuItem { Header = "复制目录路径" };
            copy.Click += delegate {
                try { Clipboard.SetText(Core.DisplayFolder(number)); Message("已复制账号 " + Two(number) + " 的目录路径。", false); }
                catch (Exception error) { Message("无法复制路径：" + error.Message, true); }
            };
            var login = new MenuItem { Header = "登录 / 重新登录保护…", IsEnabled = healthy && loginAccount == 0 && !IsBusy(number) && !deleting && !incompleteDeletes.Contains(number) };
            login.Click += async delegate { await BeginLogin(number); };
            bool incomplete = incompleteDeletes.Contains(number);
            string blocker = healthy ? Core.DeleteBlocker(number, instances, IsBusy(number), loginAccount) : scanError;
            var remove = new MenuItem { Header = incomplete ? "重试删除…" : "删除该实例…", Foreground = B("#C9604A"), IsEnabled = blocker == null && !deleting && (exists || incomplete) };
            if (blocker != null) { remove.ToolTip = blocker; ToolTipService.SetShowOnDisabled(remove, true); }
            remove.Click += async delegate { await Delete(number); };
            menu.Items.Add(reveal); menu.Items.Add(copy); menu.Items.Add(login); menu.Items.Add(new Separator()); menu.Items.Add(remove);
            menu.IsOpen = true;
        }
        void OpenFolder(string path) {
            try {
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("目录不存在：" + path);
                Process.Start(new ProcessStartInfo(IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + path + "\"") { UseShellExecute = false });
            } catch (Exception error) { Message("无法打开目录：" + error.Message, true); }
        }

        async Task BeginLogin(int number) {
            if (loginAccount != 0 || IsBusy(number)) return;
            if (!await ScanNow()) { Alert("暂时不能进入登录保护", scanError); return; }
            string failure = Core.LoginBlocker(number, instances, Busy());
            if (failure != null) { Alert("暂时不能进入登录保护", failure); return; }
            checking.Add(number); Render();
            int choice = Ask("账号 " + Two(number) + "：进入登录保护？",
                "保护期间，启动器只允许打开这个账号。随后由你在 Claude 中登录或重新登录，并核对浏览器和 Claude 中的身份。\n\n不会退出、登出其他账号或改写登录数据。请勿从开始菜单、任务栏或终端打开其他 Claude；启动器不能拦截系统登录回调。新账号会在保护中创建目录。",
                new[] { "取消", "保护并打开" }, 0, 0);
            checking.Remove(number);
            await ScanNow();
            if (choice == 1) await ProtectAndOpen(number);
        }
        async Task ProtectAndOpen(int number) {
            string failure = healthy ? Core.LoginBlocker(number, instances, Busy()) : scanError;
            if (failure != null) { Alert("状态已变化，未进入保护", failure); return; }
            try { LoginProtection.Save(ProfileDeletion.Local, number); }
            catch (Exception error) { Alert("无法保存登录保护状态", error.Message); return; }
            loginAccount = number;
            Diagnostics.Write("login_protection_begin", "slot=" + number);
            Render();
            Instance current = instances.FirstOrDefault(x => x.Number == number);
            await CheckAndOpen(number, current == null ? 0 : current.ProcessId, !Core.HasData(number));
        }
        async Task EndLogin() {
            if (loginAccount == 0 || checking.Count > 0 || pending.Count > 0) return;
            int account = loginAccount;
            checking.Add(account); Render();
            int choice = Ask("结束登录保护？", "请确认登录已完成并核对账号身份，或已取消浏览器中的登录流程。结束保护后可以打开其他账号；启动器无法验证登录结果或拦截浏览器回调。",
                new[] { "继续保护", "结束保护" }, 0, 0);
            checking.Remove(account);
            if (choice == 1) {
                string failure = null;
                try { LoginProtection.Save(ProfileDeletion.Local, 0); } catch (Exception error) { failure = error.Message; }
                if (failure != null) Alert("无法结束登录保护", failure);
                else {
                    loginAccount = 0;
                    Diagnostics.Write("login_protection_end", "slot=" + account);
                    Message("已结束登录保护。只有确认登录完成、身份正确后，才应同时打开其他账号。", false);
                }
            }
            await ScanNow();
        }

        async Task Delete(int number) {
            if (deleting || updating) return;
            if (!await ScanNow()) { Alert("暂时不能删除", scanError); return; }
            string blocker = Core.DeleteBlocker(number, instances, IsBusy(number), loginAccount);
            if (blocker != null) { Alert("暂时不能删除", blocker); return; }
            deleting = true; checking.Add(number); Render();
            Message("正在检查账号 " + Two(number) + " 的目录和运行状态…", false);
            bool confirmed = false;
            try {
                string[] paths = await Task.Run(() => { ProfileDeletion.EnsureStopped(number); return ProfileDeletion.Prepare(number, Core.Root, ProfileDeletion.Local); });
                if (closed) return;
                if (paths.Length == 0) { DeletionJournal.Clear(number, ProfileDeletion.Local); incompleteDeletes.Remove(number); Message("账号 " + Two(number) + " 已没有本地数据。", false); return; }
                if (!ConfirmDelete(number, paths)) { Message("已取消删除，账号数据未改动。", false); return; }
                // 对话框期间状态可能变化：重新扫描进程并校验路径。
                checking.Remove(number);
                if (!await ScanNow()) throw new IOException("已取消删除：" + scanError);
                blocker = Core.DeleteBlocker(number, instances, IsBusy(number), loginAccount);
                if (blocker != null) throw new IOException("已取消删除：" + blocker);
                checking.Add(number); Render();
                confirmed = true;
                DeletionJournal.Begin(number, ProfileDeletion.Local);
                Message("正在将账号 " + Two(number) + " 的数据移入回收站…", false);
                var completion = new TaskCompletionSource<int>();
                var worker = new Thread(delegate() {
                    try { completion.SetResult(ProfileDeletion.Execute(number, Core.Root, ProfileDeletion.Local, paths, () => ProfileDeletion.EnsureStopped(number), RecycleBin.Move)); }
                    catch (Exception error) { completion.SetException(error); }
                });
                worker.SetApartmentState(ApartmentState.STA); worker.Start();
                await completion.Task;
                DeletionJournal.Clear(number, ProfileDeletion.Local);
                incompleteDeletes.Remove(number); errors.Remove(number); launched.Remove(number);
                Message("账号 " + Two(number) + " 本地数据已移入回收站。", false);
            } catch (Exception error) {
                Diagnostics.Error("delete_error", error);
                if (confirmed) incompleteDeletes.Add(number);
                Message(error.Message, true);
            } finally {
                deleting = false; checking.Remove(number);
            }
            if (!closed) await ScanNow();
        }
        bool ConfirmDelete(int number, string[] paths) {
            Window dialog = BuildDeleteDialog(number, paths);
            dialog.Owner = window;
            return dialog.ShowDialog() == true;
        }
        internal Window BuildDeleteDialog(int number, string[] paths) {
            var dialog = Dialog("删除账号 " + Two(number));
            var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
            panel.Children.Add(new TextBlock { Text = "删除账号 " + Two(number) + "？", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = B("#C9604A") });
            panel.Children.Add(new TextBlock { Text = "将把以下目录移到 Windows 回收站，其中的登录状态、聊天缓存和 Claude Code 配置会一起移走。清空回收站前可以恢复。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 10), Foreground = B("#4A433C") });
            panel.Children.Add(new TextBox { Text = String.Join(Environment.NewLine + Environment.NewLine, paths), IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 180, Padding = new Thickness(10), Background = B("#FAF6EF"), Foreground = B(Ink), BorderBrush = B("#E3D9CD") });
            panel.Children.Add(new TextBlock { Text = "这不会注销云端账号，也不会删除云端聊天记录。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16), Foreground = B("#4A433C") });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", IsCancel = true, IsDefault = true, Style = (Style)window.FindResource("Btn"), Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 12, 0) };
            var confirm = new Button { Content = "移到回收站", Style = (Style)window.FindResource("Danger"), Padding = new Thickness(18, 8, 18, 8) };
            AutomationProperties.SetAutomationId(confirm, "ConfirmDelete");
            confirm.Click += delegate { dialog.DialogResult = true; };
            actions.Children.Add(cancel); actions.Children.Add(confirm); panel.Children.Add(actions);
            dialog.Loaded += delegate { cancel.Focus(); };
            return dialog;
        }
        Window Dialog(string title) {
            var dialog = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 60),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Background = B("#FFFCF7"), Foreground = B(Ink), FontFamily = window.FontFamily, FontSize = 14, Resources = window.Resources };
            dialog.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(26, 22, 26, 22) }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return dialog;
        }
        // 返回按下的按钮序号；关闭窗口或 Esc 视为 cancelIndex。
        int Ask(string title, string detail, string[] buttons, int cancelIndex, int defaultIndex) {
            var dialog = Dialog(title);
            // 主窗口隐藏在通知区域时（从托盘菜单操作），对话框居中置顶显示。
            if (window.IsLoaded && window.IsVisible) dialog.Owner = window; else { dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen; dialog.Topmost = true; }
            var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = detail ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20), Foreground = B("#4A433C"), LineHeight = 21 });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            int result = cancelIndex;
            for (int i = 0; i < buttons.Length; i++) {
                int index = i;
                var button = new Button { Content = buttons[i], Style = (Style)window.FindResource(i == defaultIndex && buttons.Length > 1 ? "Primary" : "Btn"),
                    Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(10, 0, 0, 0), IsCancel = i == cancelIndex, IsDefault = i == defaultIndex };
                button.Click += delegate { result = index; dialog.DialogResult = true; };
                actions.Children.Add(button);
                if (i == defaultIndex) dialog.Loaded += delegate { button.Focus(); };
            }
            panel.Children.Add(actions);
            dialog.ShowDialog();
            return result;
        }
        void Alert(string title, string detail) { Ask(title, detail, new[] { "知道了" }, 0, 0); }

        // 正式运行：建立托盘图标与“再次打开时显示窗口”的信号，窗口隐藏后程序继续运行，直到“退出启动器”。
        public void Show() {
            CreateTray();
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSignalName);
            ThreadPool.RegisterWaitForSingleObject(signal, delegate { window.Dispatcher.BeginInvoke(new Action(ShowMain)); }, null, -1, false);
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Show();
            Application.Current.Run();
            if (tray != null) tray.Dispose();
        }

        // 更新进度窗口：下载（真实进度）→ 校验 → 安装三步；下载期间可取消，关闭窗口视为取消。
        internal sealed class UpdateProgress {
            public Window Window; public TextBlock Source, Status, Detail; public ProgressBar Bar; public Button Cancel;
            public TextBlock[] Steps; public volatile bool Cancelled; public bool Finished;
        }
        internal UpdateProgress BuildUpdateProgress(string version) {
            var progress = new UpdateProgress();
            var dialog = Dialog("更新启动器");
            dialog.Width = 460;
            var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
            panel.Children.Add(new TextBlock { Text = "正在更新到 " + version, FontSize = 18, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "当前版本 " + Updates.DisplayVersion + " · Claude 实例和账号数据不受影响", FontSize = 12, Foreground = B(Muted), Margin = new Thickness(0, 6, 0, 4) });
            progress.Source = new TextBlock { Text = " ", FontSize = 12, Foreground = B(Muted), Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(progress.Source);
            var steps = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            string[] names = { "1  下载", "2  校验", "3  安装" };
            progress.Steps = new TextBlock[names.Length];
            for (int i = 0; i < names.Length; i++) {
                if (i > 0) steps.Children.Add(new TextBlock { Text = "—", Foreground = B("#D9CFC3"), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                progress.Steps[i] = new TextBlock { Text = names[i], FontSize = 13, Foreground = B(Faint) };
                steps.Children.Add(new Border { Child = progress.Steps[i], CornerRadius = new CornerRadius(10), Background = B("#FAF6EF"), BorderBrush = B("#E8DFD3"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 3, 10, 3) });
            }
            panel.Children.Add(steps);
            progress.Status = new TextBlock { Text = "正在准备…", TextWrapping = TextWrapping.Wrap, Foreground = B(Ink) };
            panel.Children.Add(progress.Status);
            progress.Bar = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 10, 0, 6), Foreground = B(Brown), Background = B("#EEE8E0"), BorderThickness = new Thickness(0) };
            panel.Children.Add(progress.Bar);
            progress.Detail = new TextBlock { Text = " ", FontSize = 12, Foreground = B(Muted) };
            panel.Children.Add(progress.Detail);
            progress.Cancel = new Button { Content = "取消更新", Style = (Style)window.FindResource("Btn"), Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            progress.Cancel.Click += delegate { progress.Cancelled = true; progress.Cancel.IsEnabled = false; progress.Status.Text = "正在取消…"; };
            panel.Children.Add(progress.Cancel);
            dialog.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (progress.Finished) return;
                e.Cancel = true;
                if (progress.Cancel.IsEnabled) { progress.Cancelled = true; progress.Cancel.IsEnabled = false; progress.Status.Text = "正在取消…"; }
            };
            progress.Window = dialog;
            SetUpdateStep(progress, 0);
            return progress;
        }
        static void SetUpdateStep(UpdateProgress progress, int active) {
            for (int i = 0; i < progress.Steps.Length; i++) {
                progress.Steps[i].Foreground = B(i < active ? "#3C8A68" : i == active ? "#9A6447" : Faint);
                progress.Steps[i].FontWeight = i == active ? FontWeights.SemiBold : FontWeights.Normal;
                var box = (Border)progress.Steps[i].Parent;
                box.Background = B(i == active ? "#F3E6DA" : i < active ? "#E3F3EA" : "#FAF6EF");
            }
        }
        static string Megabytes(long bytes) { return (bytes / 1048576.0).ToString("0.0") + " MB"; }
        static void CloseProgress(UpdateProgress progress) {
            if (progress == null || progress.Finished) return;
            progress.Finished = true; progress.Window.Close();
        }
        // SPEC 网络边界：只在已开启时检查本项目 GitHub Releases；失败时提供“前往 GitHub 下载”。
        async Task CheckUpdate(bool manual) {
            if (updating || closed) return;
            if (!settings.AllowUpdates) {
                if (manual) Alert("未开启检查更新", "请在系统设置中开启“允许检查 GitHub 更新”后再检查。未开启时启动器不会联网。");
                return;
            }
            if (manual && (deleting || pending.Count > 0 || checking.Count > 0)) { Message("请先等待当前操作完成。", true); return; }
            updating = true; update.IsEnabled = false; Render();
            object buttonContent = update.Content;
            if (manual) update.Content = "正在检查…";
            bool accepted = false;
            string failure = null;
            UpdateProgress progress = null;
            try {
                Release release;
                try { release = await Task.Run(() => Updates.Check(settings.AllowUpdates)); }
                catch (Exception error) { release = null; failure = error.Message; }
                if (closed) return;
                if (release != null) {
                    Diagnostics.Write("update_select", "version=" + release.version);
                    if (!Updates.IsNewer(release)) { if (manual) Alert("已是最新版本", "当前版本 " + Updates.DisplayVersion + "。"); return; }
                    if (!Updates.OsSupported(release.minimumOS, Environment.OSVersion.Version)) {
                        if (manual) Alert("新版本需要更高的系统版本", release.version + " 需要 Windows " + release.minimumOS + " 及以上。");
                        return;
                    }
                    if (deleting || pending.Count > 0 || checking.Count > 0) { Message("发现新版本 " + release.version + "，当前操作完成后可在系统设置中更新。", false); return; }
                    if (Ask("发现新版本 " + release.version, "当前版本 " + Updates.DisplayVersion + "。更新会先校验安装包，再关闭启动器、卸载旧版并安装新版，完成后重新打开；Claude 实例和账号数据不受影响。",
                        new[] { "稍后", "立即更新" }, 0, 1) != 1) return;
                    if (!settings.AllowUpdates) { Message("设置已更改，未开始更新。", true); return; }
                    accepted = true;
                    progress = BuildUpdateProgress(release.version);
                    progress.Window.Owner = window;
                    window.IsEnabled = false;
                    progress.Window.Show();
                    progress.Source.Text = "来源：GitHub";
                    progress.Status.Text = "正在从 GitHub 下载 " + release.file + "…";
                    progress.Detail.Text = "0.0 MB / " + Megabytes(release.size);
                    Message(progress.Status.Text, false);
                    var shown = progress; long total = release.size; int lastPercent = -1;
                    Action<long> report = received => {
                        int percent = (int)(received * 100 / Math.Max(1, total));
                        if (percent == lastPercent) return;
                        lastPercent = percent;
                        shown.Window.Dispatcher.BeginInvoke(new Action(() => {
                            shown.Bar.Value = percent;
                            shown.Detail.Text = Megabytes(received) + " / " + Megabytes(total) + " · " + percent + "%";
                        }));
                    };
                    string setup = null;
                    bool allowed = settings.AllowUpdates;
                    try { setup = await Task.Run(() => Updates.Download(release, allowed, report, () => shown.Cancelled)); }
                    catch (OperationCanceledException) { CloseProgress(progress); Message("已取消更新，未做任何改动。", false); return; }
                    catch (Exception error) {
                        if (progress.Cancelled) { CloseProgress(progress); Message("已取消更新，未做任何改动。", false); return; }
                        failure = error.Message;
                    }
                    if (closed) { if (setup != null) Updates.Discard(setup); return; }
                    if (setup != null) {
                        if (progress.Cancelled) { Updates.Discard(setup); CloseProgress(progress); Message("已取消更新，未做任何改动。", false); return; }
                        // 下载期间更新被关闭：安装前取消并清理临时文件，保留当前程序。
                        if (!settings.AllowUpdates) { Updates.Discard(setup); CloseProgress(progress); Message("设置在下载期间被更改，已取消更新并删除临时文件。", true); return; }
                        SetUpdateStep(progress, 1);
                        progress.Cancel.IsEnabled = false;
                        progress.Bar.IsIndeterminate = true;
                        progress.Status.Text = "正在校验安装包的签名与完整性…";
                        progress.Detail.Text = Megabytes(total) + " 已下载";
                        try { await Task.Run(() => Updates.StartSetup(setup, release)); }
                        catch (Exception error) { Diagnostics.Error("update_setup_error", error); Updates.Discard(setup); CloseProgress(progress); window.IsEnabled = true; Alert("无法安装更新", error.Message); return; }
                        SetUpdateStep(progress, 2);
                        progress.Bar.IsIndeterminate = false; progress.Bar.Value = 100;
                        progress.Status.Text = "校验通过。启动器即将关闭，由安装程序卸载旧版并安装新版，完成后会自动重新打开。";
                        progress.Detail.Text = "请稍候，不要手动打开启动器。";
                        Diagnostics.Write("update_handoff", "退出启动器，交由安装程序卸载旧版并安装新版");
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        CloseProgress(progress);
                        ExitLauncher(); return;
                    }
                }
                CloseProgress(progress);
                window.IsEnabled = true;
                Diagnostics.Write("update_failed", failure);
                AddLog("检查更新失败：" + failure);
                if ((manual || accepted) && !closed &&
                    Ask("自动更新失败", "GitHub：" + failure + "\n\n可以前往 GitHub 手动下载最新版本。", new[] { "关闭", "前往 GitHub 下载" }, 0, 1) == 1) {
                    try { Process.Start(new ProcessStartInfo(Updates.ReleasesPage) { UseShellExecute = true }); }
                    catch (Exception error) { Message("无法打开浏览器：" + error.Message, true); }
                }
            } finally {
                updating = false;
                if (!closed) { CloseProgress(progress); window.IsEnabled = true; update.Content = buttonContent; update.IsEnabled = true; Render(); }
            }
        }

        const string Styles = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Style x:Key='Btn' TargetType='Button'>
  <Setter Property='Foreground' Value='#2B2622'/><Setter Property='Background' Value='White'/>
  <Setter Property='BorderBrush' Value='#E3D9CD'/><Setter Property='BorderThickness' Value='1'/>
  <Setter Property='FontWeight' Value='Medium'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Padding' Value='10,4'/>
  <Setter Property='HorizontalContentAlignment' Value='Center'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Tag'><Setter.Value><CornerRadius>12</CornerRadius></Setter.Value></Setter>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
   <Grid>
    <Border x:Name='Box' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='{TemplateBinding Tag}'/>
    <Border x:Name='Shade' Background='Black' Opacity='0' CornerRadius='{TemplateBinding Tag}'/>
    <Border x:Name='Focus' BorderBrush='#B9825F' BorderThickness='2' CornerRadius='{TemplateBinding Tag}' Visibility='Collapsed'/>
    <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' RecognizesAccessKey='True'/>
   </Grid>
   <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Shade' Property='Opacity' Value='0.04'/></Trigger>
    <Trigger Property='IsPressed' Value='True'><Setter TargetName='Shade' Property='Opacity' Value='0.12'/></Trigger>
    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Focus' Property='Visibility' Value='Visible'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'>
     <Setter TargetName='Box' Property='Background' Value='#EFE9E1'/><Setter TargetName='Box' Property='BorderBrush' Value='#EFE9E1'/>
     <Setter Property='Foreground' Value='#A59B90'/>
    </Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style x:Key='Primary' TargetType='Button' BasedOn='{StaticResource Btn}'>
  <Setter Property='Background' Value='#A8765A'/><Setter Property='Foreground' Value='White'/><Setter Property='BorderThickness' Value='0'/>
 </Style>
 <Style x:Key='Create' TargetType='Button' BasedOn='{StaticResource Btn}'><Setter Property='Foreground' Value='#9A6447'/></Style>
 <Style x:Key='Danger' TargetType='Button' BasedOn='{StaticResource Btn}'>
  <Setter Property='Background' Value='#C9604A'/><Setter Property='Foreground' Value='White'/><Setter Property='BorderThickness' Value='0'/>
 </Style>
 <Style x:Key='Pill' TargetType='Button' BasedOn='{StaticResource Primary}'>
  <Setter Property='Tag'><Setter.Value><CornerRadius>24</CornerRadius></Setter.Value></Setter>
 </Style>
 <Style x:Key='Icon' TargetType='Button' BasedOn='{StaticResource Btn}'>
  <Setter Property='Background' Value='Transparent'/><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='0'/>
  <Setter Property='Tag'><Setter.Value><CornerRadius>8</CornerRadius></Setter.Value></Setter>
 </Style>
 <Style x:Key='Nav' TargetType='Button' BasedOn='{StaticResource Icon}'>
  <Setter Property='HorizontalContentAlignment' Value='Left'/><Setter Property='Padding' Value='20,0,0,0'/>
  <Setter Property='Tag'><Setter.Value><CornerRadius>14</CornerRadius></Setter.Value></Setter>
 </Style>
 <Style x:Key='Switch' TargetType='CheckBox'>
  <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
   <Grid Background='Transparent'>
    <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
    <ContentPresenter VerticalAlignment='Top'/>
    <Border x:Name='Track' Grid.Column='1' Width='40' Height='20' CornerRadius='10' Background='#E6DED4' BorderBrush='#9A6447' BorderThickness='0' VerticalAlignment='Top' Margin='0,2,0,0'>
     <Ellipse x:Name='Knob' Width='16' Height='16' Fill='White' Margin='2,0' HorizontalAlignment='Left' VerticalAlignment='Center'/>
    </Border>
   </Grid>
   <ControlTemplate.Triggers>
    <Trigger Property='IsChecked' Value='True'><Setter TargetName='Track' Property='Background' Value='#A8765A'/><Setter TargetName='Knob' Property='HorizontalAlignment' Value='Right'/></Trigger>
    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Track' Property='BorderThickness' Value='2'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>";
    }
}
