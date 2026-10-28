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
    sealed class LogEntry { public string Time, Level, Instance, Text; }

    // 水墨浅色界面，布局坐标与 macOS 1.6 四个页面一致（主区域 808×720，侧栏 272）。
    public sealed class LauncherUI {
        public const string Title = "Claude 多开启动器";
        const string Paper = "#FAF6F0", Ink = "#2B2622", Muted = "#6E655C", Faint = "#A59B90", Brown = "#A8765A";
        static readonly string[][] Palette = { new[] { "#FCE7D4", "#C0743A" }, new[] { "#E9E3FA", "#6A55C8" }, new[] { "#DDF0E6", "#3C8A68" }, new[] { "#FCE3DD", "#C75F47" } };
        static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        sealed class Card { public Border Root; public Ellipse Dot; public TextBlock Status, Code, Folder; public Button Action; }
        sealed class Row { public Border Root; public Ellipse Dot; public TextBlock Status, Recent; }

        readonly Window window;
        readonly Grid[] pages = new Grid[4];
        readonly Button[] nav = new Button[4];
        readonly Border indicator;
        readonly TextBlock summary, message, emptyHint, tableFooter, logSummary;
        readonly TextBlock[] stats = new TextBlock[3];
        readonly Button create, endLogin, update;
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
        static Border Toggle(bool on) {
            var knob = new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2) };
            return new Border { Width = 40, Height = 20, CornerRadius = new CornerRadius(10), Background = B(on ? Brown : "#E6DED4"), Child = knob, IsHitTestVisible = false };
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

        public LauncherUI() {
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
                At(sidebar, new Image { Source = mountains, Stretch = Stretch.Fill, OpacityMask = mask, IsHitTestVisible = false }, 0, 720 - height, 272, height);
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
                nav[i].Click += delegate { ShowPage(index); };
            }
            indicator = At(sidebar, new Border { Background = B("#B9825F"), CornerRadius = new CornerRadius(2) }, 6, 188, 4, 24);

            // 主区域背景：画面靠右，左侧洗白以保证文字可读。
            var main = new Grid { ClipToBounds = true };
            Grid.SetColumn(main, 1); root.Children.Add(main);
            ImageSource backdrop = Art("background");
            if (backdrop != null) main.Children.Add(new Image { Source = backdrop, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            var wash = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            wash.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#EDFAF6F0"), 0));
            wash.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#B8FAF6F0"), 1));
            main.Children.Add(new Rectangle { Fill = wash, IsHitTestVisible = false });
            for (int i = 0; i < 4; i++) { pages[i] = new Grid(); main.Children.Add(pages[i]); }

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
            Field(accounts, 36, 240, 268, 38, "", "搜索账号名称或目录…", Faint, false);
            Field(accounts, 316, 240, 130, 38, null, "全部状态", Ink, true);
            var refresh = At(accounts, Action("刷新状态", "Btn", "", 14), 660, 240, 112, 38);
            refresh.Click += async delegate { executable = null; await ScanNow(); };
            At(accounts, Panel(16, "#DBFFFDFA"), 36, 292, 736, 404);
            At(accounts, new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = Brushes.White, BorderBrush = B("#D9CFC3"), BorderThickness = new Thickness(1) }, 60, 306);
            string[] heads = { "账号名称", "登录邮箱", "对应实例", "状态", "最近使用", "操作" };
            double[] offsets = { 100, 250, 416, 478, 566, 660 };
            for (int i = 0; i < heads.Length; i++) At(accounts, T(heads[i], 13, "#7A6F64", FontWeights.SemiBold), 36 + offsets[i], 304);
            At(accounts, new Rectangle { Fill = B("#EFE7DC") }, 37, 336, 734, 1);
            At(accounts, new Rectangle { Fill = B("#EFE7DC") }, 37, 655, 734, 1);
            rowList = new StackPanel();
            At(accounts, new ScrollViewer { Content = rowList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 37, 337, 734, 318);
            for (int n = 1; n <= 10; n++) { rows[n] = BuildRow(n); rowList.Children.Add(rows[n].Root); }
            tableFooter = At(accounts, T("", 12, Muted, FontWeights.Normal), 60, 667, 300);
            var note = At(accounts, T("登录信息由各实例独立管理", 12, Muted, FontWeights.Normal), 500, 667, 248);
            note.TextAlignment = TextAlignment.Right;

            // 日志记录
            var logsPage = PageTitled(2, "日志记录", "查看实例启动、关闭与异常信息，便于追踪运行情况和排查问题。");
            var export = At(logsPage, Action("导出日志", "Btn", "", 14), 502, 56, 128, 44);
            export.Click += delegate { ExportLogs(); };
            var clear = At(logsPage, Action("清空日志", "Btn", "", 14), 640, 56, 132, 44);
            clear.Click += delegate { logs.Clear(); RenderLogs(); };
            Field(logsPage, 36, 140, 170, 40, "", "本次运行", Ink, true);
            Field(logsPage, 216, 140, 150, 40, null, "全部实例", Ink, true);
            Field(logsPage, 376, 140, 150, 40, null, "全部级别", Ink, true);
            Field(logsPage, 552, 140, 220, 40, "", "搜索日志内容…", Faint, false);
            At(logsPage, Panel(16, "#DBFFFDFA"), 36, 196, 736, 494);
            string[] logHeads = { "时间", "级别", "实例", "事件内容" };
            double[] logOffsets = { 24, 196, 290, 404 };
            for (int i = 0; i < 4; i++) At(logsPage, T(logHeads[i], 13, "#7A6F64", FontWeights.SemiBold), 36 + logOffsets[i], 209);
            At(logsPage, new Rectangle { Fill = B("#EFE7DC") }, 37, 240, 734, 1);
            At(logsPage, new Rectangle { Fill = B("#EFE7DC") }, 37, 640, 734, 1);
            logRows = At(logsPage, new Canvas(), 36, 241, 736, 399);
            for (int i = 0; i < 4; i++) {
                var box = At(logsPage, new Border { CornerRadius = new CornerRadius(7), Background = i == 0 ? B(Brown) : Brushes.White, BorderBrush = B("#E3D9CD"), BorderThickness = new Thickness(i == 0 ? 0 : 1), IsHitTestVisible = false }, 60 + i * 40, 652, 30, 28);
                box.Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 12, Foreground = i == 0 ? Brushes.White : B(Muted), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            logSummary = At(logsPage, T("", 12, Muted, FontWeights.Normal), 448, 658, 300);
            logSummary.TextAlignment = TextAlignment.Right;

            // 系统设置：仅日志目录、数据目录和检查更新可用，其余为静态占位。
            var settings = PageTitled(3, "系统设置", "调整启动行为、窗口管理、界面偏好以及本地数据存储。");
            At(settings, Panel(16, "#DBFFFDFA"), 36, 140, 736, 556);
            At(settings, new Rectangle { Fill = B("#EDE4D8") }, 206, 141, 1, 554);
            string[][] menu = { new[] { "常规设置", "" }, new[] { "启动行为", "" }, new[] { "外观设置", "" }, new[] { "数据与存储", "" }, new[] { "消息通知", "" }, new[] { "关于软件", "" } };
            for (int i = 0; i < menu.Length; i++) {
                var item = At(settings, new Border { CornerRadius = new CornerRadius(10), Background = i == 0 ? B("#EDDDC9") : Brushes.Transparent, IsHitTestVisible = false }, 48, 154 + i * 46, 146, 38);
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                string color = i == 0 ? "#9A6447" : "#3A332D";
                var glyph = Glyph(menu[i][1], 13, color); glyph.Width = 28; line.Children.Add(glyph);
                line.Children.Add(T(menu[i][0], 14, color, i == 0 ? FontWeights.SemiBold : FontWeights.Normal));
                item.Child = line;
            }
            string data = IOPath.Combine(ProfileDeletion.Local, "Claude-MultiLauncher");
            object[][][] sections = {
                new object[][] { new object[] { "启动与运行", "管理启动器的常规行为" },
                    new object[] { "开机自动启动", "系统登录后自动运行启动器", false },
                    new object[] { "关闭窗口后继续运行", "留在通知区域，右键图标可退出；不退出 Claude", true },
                    new object[] { "启动时恢复上次窗口", "自动恢复上一次打开的实例", false },
                    new object[] { "本地诊断日志", "启动、进程与异常记录，不含账号信息", "logs" } },
                new object[][] { new object[] { "默认行为", "新实例将沿用这些设置" },
                    new object[] { "默认打开方式", "控制 Claude 实例的窗口行为", "独立窗口" },
                    new object[] { "默认浏览器", "用于登录回调的浏览器", "系统默认浏览器" },
                    new object[] { "启动间隔", "批量启动时的间隔时间", "3 秒" } },
                new object[][] { new object[] { "界面与存储", "" },
                    new object[] { "应用语言", "界面显示语言", "简体中文" },
                    new object[] { "界面主题", "当前为水墨浅色", "浅色 · 水墨风" },
                    new object[] { "数据存储路径", "启动器偏好、日志与删除状态的位置", data } } };
            double sy = 156;
            foreach (object[][] section in sections) {
                var heading = new StackPanel { Orientation = Orientation.Horizontal };
                heading.Children.Add(T((string)section[0][0], 15, Ink, FontWeights.SemiBold));
                var sub = T((string)section[0][1], 11, "#8A7F74", FontWeights.Normal); sub.Margin = new Thickness(10, 3, 0, 0); heading.Children.Add(sub);
                At(settings, heading, 230, sy);
                double top = sy + 26, count = section.Length - 1;
                At(settings, Panel(10, "#F2FFFDFA"), 230, top, 518, count * 36);
                for (int r = 1; r < section.Length; r++) {
                    double y = top + (r - 1) * 36;
                    if (r > 1) At(settings, new Rectangle { Fill = B("#F0E8DE") }, 244, y, 490, 1);
                    At(settings, T((string)section[r][0], 13, Ink, FontWeights.SemiBold), 246, y + 2);
                    At(settings, T((string)section[r][1], 10, "#8A7F74", FontWeights.Normal), 246, y + 19, 240);
                    object control = section[r][2];
                    if (control is bool) At(settings, Toggle((bool)control), 690, y + 8);
                    else if ((string)control == "logs") {
                        var open = At(settings, Action("打开", "Btn", null, 12), 664, y + 5, 70, 26);
                        open.Click += delegate {
                            try { Diagnostics.OpenFolder(); } catch (Exception error) { Diagnostics.Error("open_logs_error", error); Alert("无法打开日志目录", error.Message); }
                        };
                    } else if ((string)control == data) {
                        Field(settings, 522, y + 4, 210, 28, null, @"%LOCALAPPDATA%\Claude-MultiLauncher", Ink, false);
                        var reveal = At(settings, new Button { Style = (Style)window.FindResource("Icon"), Content = "", ToolTip = "在资源管理器中显示：" + data, Cursor = Cursors.Hand }, 522, y + 4, 210, 28);
                        AutomationProperties.SetName(reveal, "在资源管理器中显示启动器数据目录");
                        reveal.Click += delegate { OpenFolder(data); };
                    } else Field(settings, 522, y + 4, 210, 28, null, (string)control, Ink, (string)control != "3 秒");
                }
                sy = top + count * 36 + 16;
            }
            At(settings, T("更改仅作用于本地启动器", 12, "#8A7F74", FontWeights.Normal), 230, 652);
            update = At(settings, Action("检查更新", "Btn", "", 14), 400, 642, 120, 38);
            update.Click += async delegate { await CheckUpdate(true); };
            var reset = At(settings, Action("恢复默认", "Btn", null, 14), 530, 642, 100, 38); reset.IsHitTestVisible = false; reset.Focusable = false;
            var save = At(settings, Action("保存设置", "Primary", "", 14), 640, 642, 116, 38); save.IsHitTestVisible = false; save.Focusable = false;

            ShowPage(0);
            AddLog("启动器已完成初始化");
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += async delegate { await Refresh(); };
            window.Loaded += async delegate {
                await Refresh(); timer.Start();
                // 启动 8 秒后，距上次检查超过 24 小时才自动检查；失败只记日志。
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (!closed && Updates.CheckDue()) await CheckUpdate(false);
            };
            window.Closed += delegate { closed = true; timer.Stop(); processWatch.Dispose(); Diagnostics.Write("window_closed", "正常关闭启动器"); };
            window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (deleting) { e.Cancel = true; Message("请先完成或取消当前删除操作。", true); return; }
                // SPEC 常驻图标：关闭主窗口后启动器继续运行，图标留在通知区域；只有“退出启动器”才真正退出。
                if (tray != null && !exiting) {
                    e.Cancel = true; window.Hide();
                    if (!trayHintShown) {
                        trayHintShown = true;
                        tray.ShowBalloonTip(4000, Title, "启动器仍在通知区域运行。左键单击图标打开窗口，右键可退出。", System.Windows.Forms.ToolTipIcon.Info);
                    }
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
                tray = new System.Windows.Forms.NotifyIcon { Icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize), Text = Title, Visible = true };
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
                At(canvas, new Image { Source = art, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 200, 110), 12, 12), IsHitTestVisible = false }, 185, 6, 200, 110);
                var fade = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                fade.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFFCF7"), 0)); fade.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#00FFFCF7"), 1));
                At(canvas, new Rectangle { Fill = fade, IsHitTestVisible = false }, 184, 6, 90, 110);
            }
            string[] palette = Palette[(n - 1) % 4];
            var badge = At(canvas, new Grid(), 22, 39, 44, 44);
            badge.Children.Add(new Ellipse { Fill = B(palette[0]), Stroke = B("#40" + palette[1].Substring(1)), StrokeThickness = 1 });
            badge.Children.Add(new TextBlock { Text = n.ToString(), FontSize = 20, FontWeight = FontWeights.Medium, Foreground = B(palette[1]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            At(canvas, T("账号 " + Two(n), 18, Ink, FontWeights.SemiBold), 84, 16);
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
            At(canvas, new Border { CornerRadius = new CornerRadius(4), Background = Brushes.White, BorderBrush = B("#D9CFC3"), BorderThickness = new Thickness(1), IsHitTestVisible = false }, 24, 18, 16, 16);
            string[] palette = Palette[(n - 1) % 4];
            var badge = At(canvas, new Border { CornerRadius = new CornerRadius(8), Background = B(palette[0]) }, 64, 12, 28, 28);
            badge.Child = new TextBlock { Text = Two(n), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(palette[1]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            At(canvas, T("账号 " + Two(n), 14, Ink, FontWeights.Medium), 100, 15, 110);
            var email = At(canvas, T("不读取", 13, Faint, FontWeights.Normal), 250, 16, 150);
            email.ToolTip = "启动器不读取登录凭据或账号邮箱。";
            At(canvas, T("Claude " + n, 13, Muted, FontWeights.Normal), 416, 16, 60);
            row.Dot = At(canvas, new Ellipse { Fill = B("#B9B1A7") }, 478, 22, 8, 8);
            row.Status = At(canvas, T("未运行", 13, Muted, FontWeights.SemiBold), 492, 15, 72);
            row.Recent = At(canvas, T("—", 13, Muted, FontWeights.Normal), 566, 16, 84);
            At(canvas, new Border { CornerRadius = new CornerRadius(8), Background = Brushes.White, BorderBrush = B("#E6DCCF"), BorderThickness = new Thickness(1), Child = new TextBlock { Text = "", FontFamily = Icons, FontSize = 12, Foreground = B("#8A7F74"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, IsHitTestVisible = false }, 652, 11, 30, 30);
            var more = At(canvas, MoreButton(n), 690, 11, 30, 30);
            more.BorderBrush = B("#E6DCCF"); more.BorderThickness = new Thickness(1); more.Background = Brushes.White;
            return row;
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
        internal FrameworkElement Snapshot(int index, List<Instance> state) {
            healthy = true; instances = state; ShowPage(index);
            return (FrameworkElement)((ScrollViewer)window.Content).Content;
        }

        IEnumerable<int> Busy() { return pending.Keys.Concat(checking).ToList(); }
        bool IsBusy(int number) { return pending.ContainsKey(number) || checking.Contains(number); }

        void Message(string text, bool error) {
            message.Text = text; message.Foreground = B(error ? "#C9604A" : Muted);
            if (!String.IsNullOrEmpty(text) && text != lastLogged) { lastLogged = text; AddLog(text); }
        }
        void AddLog(string text) {
            string level = new[] { "失败", "错误", "未通过", "无法" }.Any(text.Contains) ? "错误" : new[] { "超时", "保护", "未确认", "已变化", "多个" }.Any(text.Contains) ? "警告" : "信息";
            Match match = Regex.Match(text, "账号 ([0-9]{2})");
            logs.Insert(0, new LogEntry { Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Level = level, Instance = match.Success ? "Claude " + Int32.Parse(match.Groups[1].Value) : "系统", Text = text });
            RenderLogs();
        }
        void RenderLogs() {
            if (logRows == null) return;
            logRows.Children.Clear();
            if (logs.Count == 0) {
                var empty = T("暂无日志", 13, "#8A7F74", FontWeights.Normal); empty.TextAlignment = TextAlignment.Center;
                At(logRows, empty, 0, 190, 736);
            }
            for (int i = 0; i < Math.Min(10, logs.Count); i++) {
                LogEntry entry = logs[i]; double y = i * 40;
                if (i > 0) At(logRows, new Rectangle { Fill = B("#F3ECE3") }, 1, y, 734, 1);
                At(logRows, T(entry.Time, 13, "#8A7F74", FontWeights.Normal), 24, y + 10, 166);
                string[] tone = entry.Level == "错误" ? new[] { "#FBE3DF", "#C9604A" } : entry.Level == "警告" ? new[] { "#FBEFD9", "#B7832A" } : new[] { "#E3F3EA", "#3C8A68" };
                var pill = At(logRows, new Border { CornerRadius = new CornerRadius(6), Background = B(tone[0]) }, 196, y + 9, 44, 22);
                pill.Child = new TextBlock { Text = entry.Level, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B(tone[1]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                At(logRows, T(entry.Instance, 13, Ink, FontWeights.Normal), 290, y + 10, 100);
                var text = At(logRows, T(entry.Text, 13, Ink, FontWeights.Normal), 404, y + 10, 310);
                text.ToolTip = entry.Text;
            }
            logSummary.Text = "共 " + logs.Count + " 条日志 · 每页显示 10 条";
        }
        void ExportLogs() {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "Claude 启动器日志.txt", Filter = "文本文件|*.txt" };
            if (dialog.ShowDialog(window) != true) return;
            var output = new StringBuilder();
            foreach (LogEntry entry in Enumerable.Reverse(logs)) output.Append(entry.Time + "\t" + entry.Level + "\t" + entry.Instance + "\t" + entry.Text + "\r\n");
            try { File.WriteAllText(dialog.FileName, output.ToString(), new UTF8Encoding(true)); }
            catch (Exception error) { Alert("导出失败", error.Message); }
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
                row.Root.Visibility = card.Root.Visibility;
                if (!visible) continue;
                shown++;
                if (found.Count > 0) running++;
                if (broken && healthy) attention++;
                bool rowBusy = starting || isChecking;
                row.Status.Text = broken ? "异常" : rowBusy ? "处理中" : found.Count > 0 ? "运行中" : "未运行";
                row.Status.Foreground = B(broken ? "#C9604A" : rowBusy ? "#B7832A" : found.Count > 0 ? "#3C8A68" : "#8A7F74");
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
            tableFooter.Text = "共 " + shown + " 个账号 · 选中 0 项";
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
            await Launch(number);
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
        // SPEC 更新与安装：同时查自建服务与 GitHub，取较高版本；同版本自建优先，下载失败换同版本的下一个来源，都失败时提供“前往 GitHub 下载”。
        async Task CheckUpdate(bool manual) {
            if (updating || closed) return;
            if (manual && (deleting || pending.Count > 0 || checking.Count > 0)) { Message("请先等待当前操作完成。", true); return; }
            updating = true; update.IsEnabled = false; Render();
            object buttonContent = update.Content;
            if (manual) update.Content = "正在检查…";
            var failures = new List<string>();
            bool accepted = false;
            UpdateProgress progress = null;
            try {
                // 同时取所有来源的清单，取验签通过的最高版本；某个来源落后或失败不再挡住其他来源的新版本。
                SourceCheck[] checks = await Task.Run(() => Updates.CheckAll(Updates.Sources()));
                if (closed) return;
                foreach (SourceCheck check in checks) if (check.Failure != null) failures.Add(check.Source.Name + "：" + check.Failure);
                SourceCheck[] candidates = Updates.Candidates(checks);
                if (candidates.Length > 0) {
                    Updates.MarkChecked();
                    Release latest = candidates[0].Release;
                    Diagnostics.Write("update_select", "version=" + latest.version + "; sources=" + String.Join(",", candidates.Select(c => c.Source.Name)));
                    if (!Updates.IsNewer(latest)) { if (manual) Alert("已是最新版本", "当前版本 " + Updates.DisplayVersion + "。"); return; }
                    if (!Updates.OsSupported(latest.minimumOS, Environment.OSVersion.Version)) {
                        if (manual) Alert("新版本需要更高的系统版本", latest.version + " 需要 Windows " + latest.minimumOS + " 及以上。");
                        return;
                    }
                    if (deleting || pending.Count > 0 || checking.Count > 0) { Message("发现新版本 " + latest.version + "，当前操作完成后可在系统设置中更新。", false); return; }
                    if (Ask("发现新版本 " + latest.version, "当前版本 " + Updates.DisplayVersion + "。更新会先校验安装包，再关闭启动器、卸载旧版并安装新版，完成后重新打开；Claude 实例和账号数据不受影响。",
                        new[] { "稍后", "立即更新" }, 0, 1) != 1) return;
                    accepted = true;
                    progress = BuildUpdateProgress(latest.version);
                    progress.Window.Owner = window;
                    window.IsEnabled = false;
                    progress.Window.Show();
                }
                foreach (SourceCheck candidate in candidates) {
                    UpdateSource source = candidate.Source; Release release = candidate.Release;
                    SetUpdateStep(progress, 0);
                    progress.Bar.IsIndeterminate = false; progress.Bar.Value = 0;
                    progress.Source.Text = "来源：" + source.Name;
                    progress.Status.Text = "正在从" + source.Name + "下载 " + release.file + "…";
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
                    string setup;
                    try { setup = await Task.Run(() => Updates.Download(release, source, report, () => shown.Cancelled)); }
                    catch (OperationCanceledException) { CloseProgress(progress); Message("已取消更新，未做任何改动。", false); return; }
                    catch (Exception error) {
                        failures.Add(source.Name + "：" + error.Message);
                        if (progress.Cancelled) { CloseProgress(progress); Message("已取消更新，未做任何改动。", false); return; }
                        continue;
                    }
                    if (closed) return;
                    if (progress.Cancelled) { CloseProgress(progress); Message("已取消更新，未做任何改动。", false); return; }
                    SetUpdateStep(progress, 1);
                    progress.Cancel.IsEnabled = false;
                    progress.Bar.IsIndeterminate = true;
                    progress.Status.Text = "正在校验安装包的签名与完整性…";
                    progress.Detail.Text = Megabytes(total) + " 已下载";
                    try { await Task.Run(() => Updates.StartSetup(setup, release)); }
                    catch (Exception error) { Diagnostics.Error("update_setup_error", error); CloseProgress(progress); window.IsEnabled = true; Alert("无法安装更新", error.Message); return; }
                    SetUpdateStep(progress, 2);
                    progress.Bar.IsIndeterminate = false; progress.Bar.Value = 100;
                    progress.Status.Text = "校验通过。启动器即将关闭，由安装程序卸载旧版并安装新版，完成后会自动重新打开。";
                    progress.Detail.Text = "请稍候，不要手动打开启动器。";
                    Diagnostics.Write("update_handoff", "退出启动器，交由安装程序卸载旧版并安装新版");
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    CloseProgress(progress);
                    ExitLauncher(); return;
                }
                CloseProgress(progress);
                window.IsEnabled = true;
                Diagnostics.Write("update_failed", String.Join("；", failures));
                AddLog("检查更新失败：" + String.Join("；", failures));
                if ((manual || accepted) && !closed &&
                    Ask("自动更新失败", String.Join("\n", failures) + "\n\n可以前往 GitHub 手动下载最新版本。", new[] { "关闭", "前往 GitHub 下载" }, 0, 1) == 1) {
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
</ResourceDictionary>";
    }
}
