using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages;

/// <summary>真实 WPF 窗口通过生产管道读服务，检查绑定和首屏操作，再留存可复核截图。</summary>
internal static class BackgroundServiceViewChecks
{
    public static void Run(string screenshot)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var application = new System.Windows.Application();
            var model = new BackgroundServiceViewModel(CancellationToken.None);
            var page = new BackgroundServicePage(model);
            var window = new Window { Title = "CRS 独立后台服务验收", Content = page, Width = 1200, Height = 800, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await model.RefreshCommand.ExecuteAsync(null);
                    window.UpdateLayout();
                    if (model.Jobs.Count != 1 || model.History.Count == 0 || model.Status.Contains("不可用")) throw new InvalidOperationException("后台服务 DTO 未正确绑定到窗口。");
                    var labels = new HashSet<string> { "刷新状态", "立即执行", "暂停", "恢复", "取消执行", "保存计划" };
                    var buttons = Descendants(page).OfType<Button>().Where(x => x.Content is string label && labels.Contains(label)).ToArray();
                    if (buttons.Length != 6 || buttons.Any(x => !x.IsVisible || x.ActualWidth <= 0 || x.TranslatePoint(new(0, 0), window).Y > window.ActualHeight - 50)) throw new InvalidOperationException("首屏后台服务操作不可见；已找到操作数：" + buttons.Length);
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
                    using var output = File.Create(screenshot); encoder.Save(output);
                }
                catch (Exception error) { failure = error; }
                finally { window.Close(); }
            };
            application.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("后台服务窗口验收失败。", failure);
        Console.WriteLine("PASS WPF 真实窗口：生产 Named Pipes 计划/历史绑定、首屏操作与截图。");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
