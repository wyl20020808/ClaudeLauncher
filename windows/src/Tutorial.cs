using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
namespace ClaudeLauncher {
    // 新手教程（SPEC 新手教程）：气泡定位与文档渲染。文档只用 WPF 文本控件，只认标题、列表、粗体和内嵌图片，没有链接，不联网。
    internal static class Tutorial {
        const double Margin = 16, Gap = 16;
        // 优先放在高亮的下、上、右、左；都放不下时放到右下角。side 0 表示没有高亮或被迫重叠。
        internal static Rect CardRect(Rect bounds, Rect spot, Size size, out int side) {
            side = 0;
            if (spot.IsEmpty || spot.Width == 0 || spot.Height == 0) return new Rect(bounds.X + (bounds.Width - size.Width) / 2, bounds.Y + (bounds.Height - size.Height) / 2, size.Width, size.Height);
            var area = new Rect(bounds.X + Margin, bounds.Y + Margin, bounds.Width - Margin * 2, bounds.Height - Margin * 2);
            double midX = spot.X + spot.Width / 2 - size.Width / 2, midY = spot.Y + spot.Height / 2 - size.Height / 2;
            double x = Math.Min(Math.Max(midX, area.Left), area.Right - size.Width), y = Math.Min(Math.Max(midY, area.Top), area.Bottom - size.Height);
            var options = new[] { new Rect(x, spot.Bottom + Gap, size.Width, size.Height), new Rect(x, spot.Top - Gap - size.Height, size.Width, size.Height), new Rect(spot.Right + Gap, y, size.Width, size.Height), new Rect(spot.Left - Gap - size.Width, y, size.Width, size.Height) };
            for (int i = 0; i < 4; i++) if (area.Contains(options[i])) { side = i + 1; return options[i]; }
            return new Rect(area.Right - size.Width, area.Bottom - size.Height, size.Width, size.Height);
        }
        internal static Stream Resource(string file) {
            if (!Regex.IsMatch(file, @"^guide(-[a-z]+)?\.(zh|en)\.(jpg|md)$")) return null;
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeLauncher.Tutorial." + file);
        }
        internal static string Guide(bool english) {
            using (var stream = Resource(english ? "guide.en.md" : "guide.zh.md")) {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        static void Inline(InlineCollection target, string text) {
            string[] parts = text.Split(new[] { "**" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++) { if (parts[i].Length == 0) continue; if (i % 2 == 1) target.Add(new Bold(new Run(parts[i]))); else target.Add(new Run(parts[i])); }
        }
        internal static FlowDocument Document(string markdown) {
            var document = new FlowDocument { PagePadding = new Thickness(48, 36, 48, 36), Background = Appearance.Brush("#FAF6F0"), Foreground = Appearance.Brush("#2B2622"), FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI"), FontSize = 14, LineHeight = 23, ColumnWidth = 10000 };
            List list = null;
            foreach (string raw in markdown.Replace("\r", "").Split('\n')) {
                string line = raw.TrimEnd();
                if (!line.StartsWith("- ")) list = null;
                if (line.Length == 0) continue;
                Match image = Regex.Match(line, @"^!\[([^\]]*)\]\(([^)]+)\)$");
                if (image.Success) {
                    using (var stream = Resource(image.Groups[2].Value)) {
                        if (stream == null) continue; // 只显示内嵌图片，文档里写别的地址也不会去读。
                        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                        var picture = new Image { Source = bitmap, Width = 660, HorizontalAlignment = HorizontalAlignment.Left }; AutomationProperties.SetName(picture, image.Groups[1].Value);
                        document.Blocks.Add(new BlockUIContainer(picture) { Margin = new Thickness(0, 8, 0, 12) });
                    }
                }
                else if (line.StartsWith("# ")) document.Blocks.Add(new Paragraph(new Run(line.Substring(2))) { FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
                else if (line.StartsWith("## ")) document.Blocks.Add(new Paragraph(new Run(line.Substring(3))) { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8) });
                else if (line.StartsWith("- ")) {
                    if (list == null) { list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(20, 0, 0, 0) }; document.Blocks.Add(list); }
                    var item = new Paragraph { Margin = new Thickness(0, 0, 0, 4) }; Inline(item.Inlines, line.Substring(2)); list.ListItems.Add(new ListItem(item));
                }
                else { var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) }; Inline(paragraph.Inlines, line); document.Blocks.Add(paragraph); }
            }
            return document;
        }
    }
}
