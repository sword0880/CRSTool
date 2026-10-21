using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.Plugin.Wpf;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Presentation;

namespace CRS.Tests.PluginRecovery;

/// <summary>仅用合成的空视图模型渲染插件 UI；绝不访问后台管道或用户库。</summary>
internal static class UiVisualPreview
{
    public static void Render(string directory)
    {
        PluginUiResources.Validate(Application.Current.Resources);
        Directory.CreateDirectory(directory);
        var frames=0;
        var workspace=new WorkerEtfWorkspace();
        foreach (var (name, factory) in new (string, Func<FrameworkElement>)[]
        {
            ("etf", ()=>new EtfPage(new EtfViewModel(workspace,CancellationToken.None),false)),
            ("equity", ()=>new EquityPage(new EquityViewModel(workspace,CancellationToken.None),false)),
            ("exit-alert", ()=>new ExitAlertPage(false)
            {
                DataContext=new ExitAlertViewModel(workspace,CancellationToken.None)
            })
        })
        {
            foreach (var width in new[] {1040,1280,1440})
            foreach (var scale in new[] {1.0,1.25,1.5})
            {
                const int height=760;
                // 不挂到真实窗口，也不触发 Loaded，防止任何后台取数。
                var view=factory();
                var root=new Border
                {
                    Background=(Brush)Application.Current.FindResource("BrandCanvasBrush"),
                    Child=view
                };
                root.Measure(new Size(width,height));
                root.Arrange(new Rect(0,0,width,height));
                root.UpdateLayout();
                if (view.ActualWidth < width-2 || view.ActualHeight < height-2)
                    throw new InvalidOperationException(name+" 插件页面不能填满给定视口。");
                var bitmap=new RenderTargetBitmap(
                    (int)(width*scale),(int)(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var suffix=((int)(scale*100)).ToString();
                using var stream=File.Create(Path.Combine(directory,$"{name}-{width}-{suffix}.png"));
                encoder.Save(stream);
                frames++;
            }
        }
        Console.WriteLine($"PASS 国家队三类 WPF 页面：{frames} 张空状态合成截图；不调用后台服务。");
    }
}
