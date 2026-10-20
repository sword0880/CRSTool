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
                var layout=(FrameworkElement)window.Content;window.Content=null;layout.DataContext=window.DataContext;
                var surface=new System.Windows.Controls.Border{Background=(Brush)app.FindResource("BrandCanvasBrush"),Child=layout};
                Directory.CreateDirectory(directory);
                foreach(var width in new[]{1280,1040})
                {
                    surface.Measure(new(width,880));surface.Arrange(new Rect(0,0,width,880));surface.UpdateLayout();
                    var bitmap=new RenderTargetBitmap(width,180,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output=File.Create(Path.Combine(directory,$"brand-header-{width}.png"));encoder.Save(output);
                    if(width==1280)
                    {
                        var detail=new RenderTargetBitmap(412,128,192,192,PixelFormats.Pbgra32);detail.Render(surface);
                        var closeup=new PngBitmapEncoder();closeup.Frames.Add(BitmapFrame.Create(detail));
                        using var image=File.Create(Path.Combine(directory,"brand-header-detail.png"));closeup.Save(image);
                    }
                }
                Console.WriteLine("PASS 主窗口品牌布局渲染：正常及最小宽度，无插件启动或后台请求。");
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
    }
}
