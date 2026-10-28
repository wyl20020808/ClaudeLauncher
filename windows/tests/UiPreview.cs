using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeLauncher;
// 以合成进程状态离屏渲染四个页面；不扫描或启动 Claude，不打开桌面窗口。
public static class UiPreview {
    [STAThread]
    public static void Main(string[] args) {
        new Application();
        var ui = new LauncherUI();
        var state = new List<Instance> { new Instance { Number = 1, ProcessId = 111 } };
        string[] names = { "instances", "accounts", "logs", "settings" };
        Directory.CreateDirectory(args[0]);
        for (int page = 0; page < 4; page++) {
            var root = ui.Snapshot(page, state);
            var size = new Size(1080, 720);
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
            var image = new RenderTargetBitmap(1080, 720, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(args[0], names[page] + ".png"))) encoder.Save(stream);
        }
        Console.WriteLine("PASS: four offscreen pages rendered; no desktop window opened.");
    }
}
