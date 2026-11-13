using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views.AI;

/// <summary>把预览中的拖选区域换算为原图像素，不包含预览周围留白。</summary>
public partial class ScreenshotCropWindow : FluentWindow
{
    private readonly BitmapSource image;
    private Point? start;
    private Rect selection = Rect.Empty;
    public BitmapSource? Selection { get; private set; }

    public ScreenshotCropWindow(BitmapSource image)
    {
        this.image = image;
        InitializeComponent();
        Preview.Source = image;
    }

    private Rect ImageBounds()
    {
        var scale = Math.Min(CropSurface.ActualWidth / image.Width, CropSurface.ActualHeight / image.Height);
        var width = image.Width * scale;
        var height = image.Height * scale;
        return new((CropSurface.ActualWidth - width) / 2, (CropSurface.ActualHeight - height) / 2, width, height);
    }

    private void OnStart(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(CropSurface);
        if (!ImageBounds().Contains(position)) return;
        start = position;
        UpdateSelection(position);
        CropSurface.CaptureMouse();
        e.Handled = true;
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (start is not null) UpdateSelection(e.GetPosition(CropSurface));
    }

    private void OnFinish(object sender, MouseButtonEventArgs e)
    {
        if (start is null) return;
        UpdateSelection(e.GetPosition(CropSurface));
        start = null;
        CropSurface.ReleaseMouseCapture();
    }

    private void OnCaptureLost(object sender, MouseEventArgs e) => start = null;

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        start = null;
        selection = Rect.Empty;
        SelectionBorder.Visibility = Visibility.Collapsed;
        UseSelection.IsEnabled = false;
    }

    private void UpdateSelection(Point end)
    {
        var bounds = ImageBounds();
        end.X = Math.Clamp(end.X, bounds.Left, bounds.Right);
        end.Y = Math.Clamp(end.Y, bounds.Top, bounds.Bottom);
        selection = new Rect(start!.Value, end);
        Canvas.SetLeft(SelectionBorder, selection.Left);
        Canvas.SetTop(SelectionBorder, selection.Top);
        SelectionBorder.Width = selection.Width;
        SelectionBorder.Height = selection.Height;
        SelectionBorder.Visibility = Visibility.Visible;
        UseSelection.IsEnabled = selection.Width >= 4 && selection.Height >= 4;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (selection.IsEmpty) return;
        Selection = Crop(selection, ImageBounds());
        DialogResult = true;
    }

    // 像素边界夹在原图范围内，整图与反向拖选遵循相同换算。
    internal BitmapSource Crop(Rect region, Rect bounds)
    {
        var x = Math.Clamp((int)Math.Floor((region.Left - bounds.Left) / bounds.Width * image.PixelWidth), 0, image.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor((region.Top - bounds.Top) / bounds.Height * image.PixelHeight), 0, image.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling((region.Right - bounds.Left) / bounds.Width * image.PixelWidth), x + 1, image.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Bottom - bounds.Top) / bounds.Height * image.PixelHeight), y + 1, image.PixelHeight);
        return new CroppedBitmap(image, new Int32Rect(x, y, right - x, bottom - y));
    }
}
