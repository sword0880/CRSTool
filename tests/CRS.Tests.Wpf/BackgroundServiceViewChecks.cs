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
    public static void Run(string screenshot, string? pipe = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var application = new System.Windows.Application();
            var model = new BackgroundServiceViewModel(CancellationToken.None, pipe is null ? null : new CRS.Worker.Ipc.BackgroundPipeClient(pipe)){HistoryPlugin=""};
            var page = new BackgroundServicePage(model);
            var window = new Window { Title = "CRS 独立后台服务验收", Content = page, Width = 1200, Height = 800, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    // 独立窗口没有主窗体负责初始化，先加载计划和状态。
                    await model.RefreshCommand.ExecuteAsync(null);
                    // 显式应用空白范围，再验证组合筛选和零匹配不会沿用旧行。
                    await model.FilterHistoryCommand.ExecuteAsync(null);
                    if(model.History.Count==0) throw new InvalidOperationException("首次历史查询失败："+model.Status);
                    var sample=model.History.First();
                    model.HistoryPlugin=sample.PluginId; model.HistoryJob=sample.JobId;
                    await model.FilterHistoryCommand.ExecuteAsync(null);
                    if(model.History.Count==0 || model.History.Any(x=>x.PluginId!=sample.PluginId || x.JobId!=sample.JobId)) throw new InvalidOperationException("组合筛选未生效。");
                    if(model.History.Any(x=>x.PluginId!=sample.PluginId || x.JobId!=sample.JobId)) throw new InvalidOperationException("翻页丢失筛选条件。");
                    model.HistoryJob="no-such-job"; await model.FilterHistoryCommand.ExecuteAsync(null);
                    if(model.History.Count!=0) throw new InvalidOperationException("零匹配仍显示旧历史。");
                    model.HistoryPlugin=""; model.HistoryJob=""; await model.FilterHistoryCommand.ExecuteAsync(null);
                    // 展示实际新增的筛选页，避免截图只覆盖默认服务计划页。
                    Descendants(page).OfType<TabControl>().Single().SelectedIndex=1;
                    window.UpdateLayout();
                    if(Descendants(page).OfType<ComboBox>().Count(x=>x.IsVisible && x.IsEditable)!=2) throw new InvalidOperationException("插件及任务筛选输入不可见。");
                    if (model.Jobs.Count < 1 || model.History.Count == 0 || model.Status.Contains("不可用")) throw new InvalidOperationException("后台服务 DTO 未正确绑定到窗口。");
                    var labels = new HashSet<string> { "刷新状态", "立即执行", "暂停", "恢复", "取消执行" };
                    var buttons = Descendants(page).OfType<Button>().Where(x => x.Content is string label && labels.Contains(label)).ToArray();
                    if (buttons.Length != 5 || buttons.Any(x => !x.IsVisible || x.ActualWidth <= 0 || x.TranslatePoint(new(0, 0), window).Y > window.ActualHeight - 50)) throw new InvalidOperationException("首屏后台服务操作不可见；已找到操作数：" + buttons.Length);
                    if(page.FindName("MoreRecordsButton") is not null||
                        ((FrameworkElement)page.FindName("ConfirmRequestButton")).IsVisible)
                        throw new InvalidOperationException("分页或重新确认按钮显示条件错误。");
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
