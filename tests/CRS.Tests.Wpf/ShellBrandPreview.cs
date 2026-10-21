using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;

/// <summary>脱离窗口生命周期渲染真实主窗口布局，不启动插件、后台连接或个人保险库。</summary>
internal static class ShellBrandPreview
{
    public static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                var app=new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
                var window=new MainWindow(new ShellViewModel(new FakeUseCases(),new SilentInteraction()));
                var button=window.FindName("NavigationToggleButton") as System.Windows.Controls.Button
                    ??throw new InvalidOperationException("主窗口缺少统一导航按钮。");
                var brand=window.FindName("BrandIdentity") as FrameworkElement
                    ??throw new InvalidOperationException("主窗口缺少品牌名称。");
                var header=window.FindName("BrandHeader") as FrameworkElement
                    ??throw new InvalidOperationException("主窗口缺少统一标题栏布局。");
                var pluginSurface=window.FindName("PluginSurface") as FrameworkElement
                    ??throw new InvalidOperationException("主窗口缺少插件承载区域。");
                var layout=(FrameworkElement)window.Content;window.Content=null;layout.DataContext=window.DataContext;
                var surface=new System.Windows.Controls.Border{Background=(Brush)app.FindResource("BrandCanvasBrush"),Child=layout};
                Directory.CreateDirectory(directory);
                foreach(var width in new[]{1280,1040})
                {
                    foreach(var expanded in new[]{true,false})
                    {
                        // 使用真实Button.Click事件而非直接改IsPaneOpen，确保唯一按钮和原导航状态同源。
                        if(window.IsNavigationExpanded!=expanded)
                            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,button));
                        surface.Measure(new(width,880));surface.Arrange(new Rect(0,0,width,880));surface.UpdateLayout();
                        var expected=expanded?206:56;
                        if(window.IsNavigationExpanded!=expanded||Math.Abs(header.ActualWidth-expected)>1
                            ||Math.Abs(pluginSurface.Margin.Left-expected)>1
                            ||brand.Visibility!=(expanded?Visibility.Visible:Visibility.Collapsed)
                            ||brand.IsHitTestVisible||!button.IsHitTestVisible)
                            throw new InvalidOperationException("品牌栏折叠状态、插件偏移、拖拽穿透或按钮命中区域未同步。");
                        if((string?)button.ToolTip!=(expanded?"收起侧边栏":"展开侧边栏"))
                            throw new InvalidOperationException("导航按钮悬停文案与展开状态不一致。");
                        var suffix=expanded?"": "-collapsed";
                        var bitmap=new RenderTargetBitmap(width,180,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output=File.Create(Path.Combine(directory,$"brand-header-{width}{suffix}.png"));encoder.Save(output);
                        if(width==1280)
                        {
                            var detail=new RenderTargetBitmap(412,128,192,192,PixelFormats.Pbgra32);detail.Render(surface);
                            var closeup=new PngBitmapEncoder();closeup.Frames.Add(BitmapFrame.Create(detail));
                            using var image=File.Create(Path.Combine(directory,$"brand-header-detail{suffix}.png"));closeup.Save(image);
                        }
                    }
                }
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,button));
                if(!window.IsNavigationExpanded)throw new InvalidOperationException("二次折叠操作未恢复展开状态。");
                Console.WriteLine("PASS 主窗口单行品牌栏：1280/1040宽度展开与收起、唯一折叠按钮、插件偏移、品牌图文和恢复操作。");
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
    }
}
