using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages.Performance;

/// <summary>验证绩效只读子视图可独立排版，完全使用合成模型，不访问真实资料库。</summary>
internal static class PerformanceComponentPreview
{
    public static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(() =>
        {
            try
            {
                var app=new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
                var shell=new ShellViewModel(new FakeUseCases(),new SilentInteraction());
                Directory.CreateDirectory(directory);
                foreach (var (name,view) in new (string, UserControl)[]
                {
                    ("exposure",new PerformanceExposureView()),
                    ("history",new PerformanceHistoryView())
                })
                {
                    view.DataContext=shell.Performance;
                    foreach(var width in new[]{1040,1280,1440})
                    {
                        var root=new Border{Background=(Brush)app.FindResource("BrandCanvasBrush"),Child=view};
                        root.Measure(new(width,600));root.Arrange(new Rect(0,0,width,600));root.UpdateLayout();
                        if(view.ActualWidth<width-2||view.ActualHeight<598)
                            throw new InvalidOperationException($"绩效 {name} 子视图没有填满视口：{view.ActualWidth}x{view.ActualHeight}");
                        var table=Find<DataGrid>(view)??throw new InvalidOperationException("绩效子视图缺少绑定表格："+name);
                        if(table.Columns.Count<5||table.ItemsSource is null)
                            throw new InvalidOperationException("绩效子视图丢失表格列或 ItemsSource："+name);
                        // 空状态不能把表头自动压缩为单个汉字；列可水平滚动但文字须可辨。
                        var narrow=table.Columns.Where(column=>column.MinWidth<70 || column.ActualWidth<70).ToArray();
                        if(narrow.Length>0)
                            throw new InvalidOperationException("绩效 "+name+" 列宽过窄："+
                                string.Join("，",narrow.Select(c=>$"{c.Header}:{c.ActualWidth:F0}/{c.MinWidth:F0}")));
                        var image=new RenderTargetBitmap(width,600,96,96,PixelFormats.Pbgra32);
                        image.Render(root);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                        using var file=File.Create(Path.Combine(directory,$"performance-{name}-{width}.png"));
                        encoder.Save(file);
                        root.Child=null;
                    }
                }
                Console.WriteLine("PASS 绩效组件化：原币资产/历史表格绑定及 1040/1280/1440 三种宽度合成截图。");
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();thread.Join();
        if(failure is not null)throw failure;
    }

    private static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found)return found;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            if(Find<T>(VisualTreeHelper.GetChild(root,i)) is { } result)return result;
        return null;
    }
}
