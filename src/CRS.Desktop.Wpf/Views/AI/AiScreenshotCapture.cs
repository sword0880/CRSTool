using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.AI.Models;

namespace CRS.DesktopClient.Views.AI;

/// <summary>仅渲染当前应用的业务区域，按比例限制像素和编码体积。</summary>
internal static class AiScreenshotCapture
{
    private const double MaxDimension = 2000;

    public static BitmapSource? Capture(FrameworkElement target)
    {
        var size = target.RenderSize;
        if (size.Width < 1 || size.Height < 1) return null;
        var scale = Math.Min(VisualTreeHelper.GetDpi(target).DpiScaleX, MaxDimension / Math.Max(size.Width, size.Height));
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        var image = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(target), null, new Rect(0, 0, pixelWidth, pixelHeight));
        image.Render(drawing);
        image.Freeze();
        return image;
    }

    public static byte[]? Encode(BitmapSource image)
    {
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0) return null;
        var scale = Math.Min(1, MaxDimension / Math.Max(image.PixelWidth, image.PixelHeight));
        if (scale < 1) image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        // 编码前压平透明背景，截图文字在不同模型端保持可读。
        var visual = new DrawingVisual();
        var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, bounds);
            context.DrawImage(image, bounds);
        }
        var raster = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        raster.Render(visual);
        foreach (var quality in new[] { 90, 78, 62 })
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(raster));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length <= AiChatRequest.MaxScreenshotBytes) return stream.ToArray();
        }
        return null;
    }
}
