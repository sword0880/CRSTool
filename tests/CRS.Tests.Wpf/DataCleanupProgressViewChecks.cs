using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Settings;
using CRS.Maintenance;

/// <summary>真实清理视图的活动扫描、校验与确定进度渲染；仅使用合成资料，不能删除真实文件。</summary>
internal static class DataCleanupProgressViewChecks
{
    public static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var app=new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
            var gateway=new DataCleanupViewChecks.ProgressGateway();var model=new DataCleanupViewModel(gateway);
            var view=new DataCleanupSettingsView{DataContext=new{Cleanup=model}};
            var content=new StackPanel{Margin=new Thickness(20)};
            content.Children.Add(new TextBlock{Text="运行资料清理",FontSize=28,FontWeight=FontWeights.Bold});
            content.Children.Add(new TextBlock{Text="合成资料布局检查 · 扫描不会删除资料",Margin=new Thickness(0,8,0,22)});content.Children.Add(view);
            var window=new Window{Title="清理进度隔离检查",Content=new ScrollViewer{Content=content,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},
                Width=1040,Height=760,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,Background=(Brush)app.FindResource("BrandCanvasBrush")};
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    Directory.CreateDirectory(directory);var scan=model.ScanCommand.ExecuteAsync(null);
                    var scanning=DataCleanupViewChecks.ProgressGateway.Ready(gateway.Id) with{State="Scanning",CanExecute=false,Entries=[],Message="正在校验需要保留的历史副本。",
                        Progress=new("VerifyingBackups","maintenance/scan-fixture/snapshot/plugins/crs.national-team/data/market.db",12804,300*1024*1024,520,120*1024*1024)};
                    gateway.Report(scanning);await Task.Delay(1200);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var bar=(ProgressBar)view.FindName("CleanupProgress");
                    Capture(window,Path.Combine(directory,"scanning-diagnostic.png"));
                    if(!model.Busy||!bar.IsVisible||!bar.IsIndeterminate||model.ElapsedText=="已用时 00:00"||!model.Summary.Contains("已扫描"))
                        throw new InvalidOperationException($"扫描状态：Busy={model.Busy}, Visible={bar.IsVisible}, Indeterminate={bar.IsIndeterminate}, Elapsed={model.ElapsedText}, Summary={model.Summary}。");
                    foreach(var width in new[]{640,1040}){window.Width=width;Capture(window,Path.Combine(directory,$"scanning-{width}.png"));}
                    gateway.Complete(DataCleanupViewChecks.ProgressGateway.Ready(gateway.Id));await scan;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if(bar.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("扫描完成后活动进度未收起。");
                    var execute=model.ExecuteCommand.ExecuteAsync(null);
                    var running=DataCleanupViewChecks.ProgressGateway.Ready(gateway.Id) with{State="Running",CanExecute=false,Message="正在重新核对清理范围与需要保留的副本。",
                        Progress=new("VerifyingCleanup","maintenance/scan-fixture/snapshot/scheduler-config.db",VerifiedBytes:3*1024*1024)};
                    gateway.Report(running);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if(!bar.IsIndeterminate)throw new InvalidOperationException("删除前校验错误显示确定百分比。");
                    Capture(window,Path.Combine(directory,"verifying-1040.png"));
                    running=running with{DeletedFiles=8,DeletedBytes=8*1024*1024,Message="正在逐文件清理已确认弃用的副本。",Progress=running.Progress! with{Phase="Deleting"}};
                    gateway.Report(running);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if(bar.IsIndeterminate||bar.Value!=40)throw new InvalidOperationException("实际删除进度未按已完成文件数呈现。");
                    foreach(var width in new[]{640,1040}){window.Width=width;Capture(window,Path.Combine(directory,$"deleting-{width}.png"));}
                    gateway.Complete(running with{State="Completed",DeletedFiles=20,DeletedBytes=20*1024*1024,Message="合成清理完成；没有操作真实文件。"});await execute;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if(model.Busy||bar.Visibility!=Visibility.Collapsed||!model.Summary.Contains("清理完成"))throw new InvalidOperationException("完成状态未正确结束活动进度。");
                    Capture(window,Path.Combine(directory,"completed-1040.png"));
                }
                catch(Exception error){failure=error;}
                finally{model.StopWaiting();window.Close();}
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("清理进度界面验收失败。",failure);
        Console.WriteLine("PASS 清理进度：真实WPF视图、每秒用时、扫描活动进度、清理40%与结束收起；640/1040 DIP共6张合成渲染。");
    }
    private static void Capture(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }
}
