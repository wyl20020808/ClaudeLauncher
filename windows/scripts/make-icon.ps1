$ErrorActionPreference = 'Stop'
# 由用户自有图标 resources/icon/icon_full_1024.png 生成 assets/app.ico（16、20、24、32、40、48、64、256）。
# 16～24 像素用简化图形（橙色圆角方块 + 白色光芒），原图在小尺寸下细节会糊成一团；32 及以上由原图高质量缩小。
# 16～64 写成 32 位 DIB（System.Drawing.Icon / 托盘兼容性最好），256 写成 PNG。生成结果提交到仓库，日常构建不需要运行本脚本。
$projectRoot = Split-Path $PSScriptRoot -Parent
$source = Join-Path (Split-Path $projectRoot -Parent) 'resources/icon/icon_full_1024.png'
$output = Join-Path $projectRoot 'assets/app.ico'
$preview = Join-Path $projectRoot 'artifacts/icon-preview'
Add-Type -AssemblyName PresentationCore, WindowsBase
Add-Type -ReferencedAssemblies PresentationCore, WindowsBase, System.Xaml -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
public static class IconMaker {
    static readonly Color Orange = Color.FromRgb(0xFC, 0x7E, 0x3A);
    static BitmapSource Render(int size, Action<DrawingContext> draw) {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen()) draw(context);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
    }
    public static BitmapSource Scaled(BitmapSource original, int size) {
        return Render(size, c => c.DrawImage(original, new Rect(0, 0, size, size)));
    }
    // 简化小图：圆角橙色底 + 8 道白色光芒，线宽按像素取整保证清晰。
    public static BitmapSource Simple(int size) {
        return Render(size, c => {
            double r = size * 0.22;
            c.DrawRoundedRectangle(new SolidColorBrush(Orange), null, new Rect(0, 0, size, size), r, r);
            double center = size / 2.0, inner = size * 0.12, outer = size * 0.36;
            var pen = new Pen(Brushes.White, Math.Max(1.6, size / 9.0)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i < 8; i++) {
                double a = Math.PI * i / 4 + Math.PI / 8;
                c.DrawLine(pen, new Point(center + inner * Math.Cos(a), center + inner * Math.Sin(a)), new Point(center + outer * Math.Cos(a), center + outer * Math.Sin(a)));
            }
        });
    }
    static byte[] Dib(BitmapSource image) {
        int size = image.PixelWidth, stride = size * 4;
        var pixels = new byte[stride * size];
        image.CopyPixels(pixels, stride, 0);
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream)) {
            writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((short)1); writer.Write((short)32);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for (int y = size - 1; y >= 0; y--) writer.Write(pixels, y * stride, stride);
            int maskStride = ((size + 31) / 32) * 4;
            writer.Write(new byte[maskStride * size]); // 透明度由 alpha 通道决定，AND 掩码全 0。
            return stream.ToArray();
        }
    }
    static byte[] Png(BitmapSource image) {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
    }
    public static void Write(string path, IList<BitmapSource> images) {
        var data = new List<byte[]>();
        foreach (var image in images) data.Add(image.PixelWidth >= 256 ? Png(image) : Dib(image));
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream)) {
            writer.Write((short)0); writer.Write((short)1); writer.Write((short)images.Count);
            int offset = 6 + 16 * images.Count;
            for (int i = 0; i < images.Count; i++) {
                int size = images[i].PixelWidth;
                writer.Write((byte)(size >= 256 ? 0 : size)); writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((short)1); writer.Write((short)32);
                writer.Write(data[i].Length); writer.Write(offset); offset += data[i].Length;
            }
            foreach (var bytes in data) writer.Write(bytes);
        }
    }
    public static void SavePng(BitmapSource image, string path) { File.WriteAllBytes(path, Png(image)); }
}
'@
$stream = [IO.File]::OpenRead($source)
try { $original = [Windows.Media.Imaging.BitmapDecoder]::Create($stream, 'None', 'OnLoad').Frames[0] } finally { $stream.Close() }
$images = New-Object 'System.Collections.Generic.List[System.Windows.Media.Imaging.BitmapSource]'
[IO.Directory]::CreateDirectory($preview) | Out-Null
foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
    $image = if ($size -le 24) { [IconMaker]::Simple($size) } else { [IconMaker]::Scaled($original, $size) }
    $images.Add($image)
    [IconMaker]::SavePng($image, (Join-Path $preview ('icon-' + $size + '.png')))
}
[IconMaker]::Write($output, $images)
Write-Output ('Wrote ' + $output + ' (' + (Get-Item -LiteralPath $output).Length + ' bytes); previews in ' + $preview)
