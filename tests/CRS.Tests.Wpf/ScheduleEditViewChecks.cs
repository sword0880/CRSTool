using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.Worker.Abstractions;

internal static class ScheduleEditViewChecks
{
    internal static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                foreach(var resource in new[]{"BrandPalette","SharedStyles"})app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/"+resource+".xaml")});
                Directory.CreateDirectory(directory);
                foreach(var enabled in new[]{false,true})foreach(var width in new[]{620,720})
                {
                    var model=new ScheduleEditViewModel(new(CancellationToken.None),new TaskDefinition("crs.national-team","equity-auto-sync","机构直接持股自动发现与更新","0 15 0 * * ?","China Standard Time",enabled,2,false));
                    var window=new ScheduleEditWindow(model){Width=width,Height=820};
                    try
                    {
                        window.Show();window.UpdateLayout();
                        var status=(TextBlock)window.FindName("TaskEnabledStatus");var enable=(Button)window.FindName("EnableTaskButton");var disable=(Button)window.FindName("DisableTaskButton");
                        if(status.Text!=(enabled?"已启用":"已禁用")||enable.IsEnabled==enabled||disable.IsEnabled!=enabled||enable.Command!=model.EnableTaskCommand||disable.Command!=model.DisableTaskCommand)
                            throw new Exception("任务详情状态或启用/禁用按钮绑定错误。");
                        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(window);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output=File.Create(Path.Combine(directory,$"schedule-{(enabled?"enabled":"disabled")}-{width}.png"));encoder.Save(output);
                    }
                    finally{window.Close();}
                }
                app.Shutdown();
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new Exception("任务详情启用/禁用界面验收失败。",failure);
        Console.WriteLine("PASS 任务详情启用/禁用状态和操作绑定，620/720 DIP共4张合成截图，未修改正式任务。");
    }
}
