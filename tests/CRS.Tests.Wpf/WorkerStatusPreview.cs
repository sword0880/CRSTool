using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages;

/// <summary>无 IPC 的后台状态页视觉回归，覆盖正常/未知/故障语义与紧凑宽度。</summary>
internal static class WorkerStatusPreview
{
    public static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(() =>
        {
            try
            {
                var application=new Application();
                application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
                application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                application.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
                application.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
                Directory.CreateDirectory(directory);
                foreach(var sample in new[]
                {
                    (Name:"running",Tone:"Success",Label:"运行正常",Message:"后台服务正常；公开任务独立于窗口与税务登录运行。"),
                    (Name:"unavailable",Tone:"Warning",Label:"无法确认服务状态",Message:"后台服务不可用或响应超时；所列内容为上次状态快照。"),
                    (Name:"storage-fault",Tone:"Error",Label:"后台存储故障",Message:"后台存储故障，已停止新任务；请修复并重启服务核验。")
                })
                {
                    var model=new BackgroundServiceViewModel(CancellationToken.None)
                    {
                        HealthTone=sample.Tone, HealthLabel=sample.Label,Status=sample.Message,
                        SnapshotTime="状态采样：2026-10-21 11:00:00 · 合成数据"
                    };
                    var page=new WorkerStatusPage(model);
                    foreach(var width in new[]{760d,960d,1180d})
                    {
                        page.Measure(new Size(width,780));
                        page.Arrange(new Rect(0,0,width,780));
                        page.UpdateLayout();
                        if(page.ActualWidth<=0||page.ActualHeight<=0)
                            throw new InvalidOperationException("服务状态页布局失败。");
                        var labels=Descendants(page).OfType<TextBlock>().ToArray();
                        if(!labels.Any(x=>x.Text==sample.Label && x.Visibility==Visibility.Visible) || !labels.Any(x=>x.Text==sample.Message && x.Visibility==Visibility.Visible))
                            throw new InvalidOperationException($"健康状态或反馈未呈现：{sample.Name}, {width}; labels={string.Join(" | ",labels.Select(x=>x.Text).Where(x=>!string.IsNullOrWhiteSpace(x)).Take(16))}");
                        var bitmap=new RenderTargetBitmap((int)width,780,96,96,PixelFormats.Pbgra32);
                        bitmap.Render(page);
                        var encoder=new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output=File.Create(Path.Combine(directory,$"worker-status-{sample.Name}-{(int)width}.png"));
                        encoder.Save(output);
                    }
                }
                Console.WriteLine("PASS 后台服务状态页：三种语义状态、760/960/1180宽度、无需连接Windows服务。");
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if(failure is not null)throw failure;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            yield return child;
            foreach(var nested in Descendants(child))yield return nested;
        }
    }
}
