using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeLauncher;
// 以合成进程状态离屏渲染四个页面与更新进度窗口；不扫描或启动 Claude，不打开桌面窗口。
public static class UiPreview {
    static void Save(FrameworkElement root, Size size, string path) {
        root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
    [STAThread]
    public static void Main(string[] args) {
        new Application();
        var ui = new LauncherUI();
        var state = new List<Instance> { new Instance { Number = 1, ProcessId = 111 } };
        string[] names = { "instances", "accounts", "logs", "settings" };
        Directory.CreateDirectory(args[0]);
        for (int page = 0; page < 4; page++) Save(ui.Snapshot(page, state), new Size(1080, 720), Path.Combine(args[0], names[page] + ".png"));
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
        Console.WriteLine("PASS: four pages and two update states rendered offscreen; no desktop window opened.");
    }
}
