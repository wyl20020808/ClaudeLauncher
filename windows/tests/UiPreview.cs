using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeLauncher;
public static class UiPreview {
    [STAThread]
    public static void Main(string[] args) {
        new Application();
        var ui = new LauncherUI();
        var type = typeof(LauncherUI); var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        type.GetField("healthy",flags).SetValue(ui,true);
        type.GetField("instances",flags).SetValue(ui,Core.Scan());
        type.GetMethod("Render",flags).Invoke(ui,null);
        var window = (Window)type.GetField("window",flags).GetValue(ui);
        var root = (FrameworkElement)window.Content;
        Directory.CreateDirectory(args[0]);
        foreach (var size in new[] {new Size(944,780),new Size(804,520)}) {
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
            var image = new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using(var stream=File.Create(Path.Combine(args[0],"dark-"+size.Width+"x"+size.Height+".png")))encoder.Save(stream);
        }
        Console.WriteLine("PASS: two offscreen layouts rendered; no desktop window opened.");
    }
}
