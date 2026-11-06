using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.DesktopClient.Views.Settings;

internal static class CleanupTaskViewChecks
{
    private sealed class Settings:IDesktopSettingsService
    {public DesktopSettings Load()=>new("D:\\synthetic-data","D:\\synthetic-logs");public Task SaveAsync(DesktopSettings settings)=>Task.CompletedTask;}
    internal static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var app=new Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
            var gateway=new CleanupTaskModelChecks.Gateway();var shell=new ShellViewModel(new FakeUseCases(),new SilentInteraction(),new Settings(),cleanup:gateway);
            var window=new MainWindow(shell,false);Directory.CreateDirectory(directory);
            window.Loaded+=async(_,_)=>
            {
                try{await VerifyAsync(window,shell,gateway,directory);}
                catch(Exception error){failure=error;}
                finally{window.Close();}
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("后台清理任务界面验收失败。",failure);
        Console.WriteLine("PASS 设置页只有提交入口，完成提醒在主窗口；确认后收起，640/1040 DIP布局通过，未连接正式后台。");
    }
    private static async Task VerifyAsync(MainWindow window,ShellViewModel shell,CleanupTaskModelChecks.Gateway gateway,string directory)
    {
        var model=shell.Settings!.Cleanup;await model.SubmitTaskCommand.ExecuteAsync(null);gateway.Complete();await model.RefreshTaskCommand.ExecuteAsync(null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var notice=(Border)window.FindName("CleanupTaskNotification");
        if(notice.Visibility!=Visibility.Visible||!model.HasTaskNotification)throw new Exception("设置窗口关闭时主窗口没有完成提醒。");
        window.Width=1040;Capture(window,Path.Combine(directory,"main-notification-1040.png"));
        var page=new DataCleanupSettingsView{DataContext=shell.Settings,Margin=new Thickness(24)};
        var host=new Window{Content=page,Width=640,Height=340,Background=(Brush)Application.Current.Resources["BrandCanvasBrush"]};host.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var content=(StackPanel)page.Content;
        if(content.Children.OfType<Button>().Count()!=1||content.Children.OfType<ProgressBar>().Any()||content.Children.OfType<ItemsControl>().Any())throw new Exception("设置窗体仍显示清理进度或结果控件。");
        if(content.Children.OfType<TextBlock>().Any(x=>x.Text.Contains("17 个文件")))throw new Exception("后台完成结果出现在设置窗体。");
        Capture(host,Path.Combine(directory,"settings-submit-only-640.png"));host.Close();
        await model.DismissTaskNotificationCommand.ExecuteAsync(null);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if(notice.Visibility!=Visibility.Collapsed)throw new Exception("已读提醒没有收起。");
        Capture(window,Path.Combine(directory,"main-notification-read-1040.png"));
    }
    private static void Capture(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
