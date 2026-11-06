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
using CRS.DesktopClient.Views.Pages;
using CRS.Worker.Ipc;
using CRS.Maintenance;

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
            using var stop=new CancellationTokenSource();var pipe="CRS.CleanupTaskView."+Guid.NewGuid().ToString("N");
            var server=new BackgroundPipeServer(new Control(),pipe).RunAsync(stop.Token);
            var gateway=new CleanupTaskModelChecks.Gateway();var shell=new ShellViewModel(new FakeUseCases(),new SilentInteraction(),new Settings(),cleanup:gateway,workerClient:new(pipe));
            var window=new MainWindow(shell,false);Directory.CreateDirectory(directory);
            window.Loaded+=async(_,_)=>
            {
                try{shell.StatusBar.Start(stop.Token);await VerifyAsync(window,shell,gateway,directory,pipe);}
                catch(Exception error){failure=error;}
                finally{window.Close();}
            };
            app.Run(window);
            stop.Cancel();try{server.GetAwaiter().GetResult();}catch(OperationCanceledException){}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("后台清理任务界面验收失败。",failure);
        Console.WriteLine("PASS 底栏仅显示运行/等待总数，清理计入对应数量；任务中心显示清理核验及实际扫描量；完成提醒、确认收起及640/1040 DIP布局通过，未连接正式后台。");
    }
    private static async Task VerifyAsync(MainWindow window,ShellViewModel shell,CleanupTaskModelChecks.Gateway gateway,string directory,string pipe)
    {
        var model=shell.Settings!.Cleanup;await model.SubmitTaskCommand.ExecuteAsync(null);
        var until=DateTimeOffset.UtcNow.AddSeconds(10);
        while(shell.StatusBar.TaskLabel!="运行 0 · 等待 1")
        {if(DateTimeOffset.UtcNow>until)throw new Exception("排队中的清理被误算为运行中，或底栏未取得状态。");await Task.Delay(20);}
        window.Width=1040;Capture(window,Path.Combine(directory,"main-cleanup-queued-1040.png"));
        gateway.Overview=gateway.Overview with{Current=gateway.Overview.Current! with{State="Scanning",Progress=new("VerifyingBackups",null,859,119382731,VerifiedBytes:4096)}};
        await model.RefreshTaskCommand.ExecuteAsync(null);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var footer=(Wpf.Ui.Controls.Button)window.FindName("FooterTasksButton");
        if(footer.Content?.ToString()!="运行 1 · 等待 0"||shell.StatusBar.TaskLabel!="运行 1 · 等待 0")throw new Exception("扫描清理没有立即计入底栏总运行数，或额外显示了清理名称与阶段。");
        Capture(window,Path.Combine(directory,"main-cleanup-verifying-1040.png"));
        using(var lifetime=new CancellationTokenSource())
        {
            gateway.Overview=gateway.Overview with{AutomaticPlan=new("Running",CRS.Timing.BeijingClock.Now.AddHours(1),300,30,86400,3600,14,1)};
            var activity=new BackgroundServiceViewModel(lifetime.Token,new(pipe),model);await activity.RefreshTaskActivityAsync();
            var tasks=new BackgroundServicePage(activity);tasks.ShowCurrentTasks();
            var taskWindow=new Window{Content=tasks,Width=1040,Height=760,Background=(Brush)Application.Current.Resources["BrandCanvasBrush"]};
            try
            {
                taskWindow.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var grid=(DataGrid)tasks.FindName("RunningTasksGrid");
                if(grid.Items.Count!=1||activity.RunningTasks[0].ReasonText.Contains("859")!=true)throw new Exception("实际任务中心没有列出清理核验进展。");
                Capture(taskWindow,Path.Combine(directory,"task-center-cleanup-verifying-1040.png"));
                ((TabControl)tasks.FindName("TaskCenterTabs")).SelectedIndex=0;
                activity.Jobs.Add(new("crs.national-team","official-share-sync","官方ETF份额同步（合成）","0 15 23 * * ?","China Standard Time",true,3,false));
                activity.Jobs.Add(new("crs.national-team","evidence-integrity","证据完整性巡检（合成）","0 30 2 * * ?","China Standard Time",true,2,false));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var plans=(DataGrid)tasks.FindName("PlansGrid");
                var maintenance=activity.PlanRows.Single(x=>x.IsMaintenance);
                if(tasks.FindName("AutomaticCleanupPlanCard") is not null||!plans.Items.Contains(maintenance)||!maintenance.Cron.Contains("24")||maintenance.NextDueText=="未确认")throw new Exception("自动清理没有作为普通一行出现在统一任务列表。");
                activity.SelectedPlan=maintenance;
                if(activity.PauseCommand.CanExecute(null)||activity.ResumeCommand.CanExecute(null))throw new Exception("维护任务错误地允许发送插件启停命令。");
                Capture(taskWindow,Path.Combine(directory,"task-center-maintenance-plan-1040.png"));
                var detail=new MaintenancePlanWindow(model,lifetime.Token){Owner=taskWindow};
                try{detail.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Capture(detail,Path.Combine(directory,"maintenance-plan-detail-620.png"));}
                finally{detail.Close();}
            }
            finally{lifetime.Cancel();taskWindow.Close();}
        }
        gateway.Complete();await model.RefreshTaskCommand.ExecuteAsync(null);
        if(shell.StatusBar.TaskLabel!="运行 0 · 等待 0")throw new Exception("清理完成后底栏没有撤回运行状态。");
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
    private sealed class Control:IBackgroundControl
    {
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            var now=CRS.Timing.BeijingClock.Now;
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",
                Health:new("Running",now,now,123,new(4,0,0,12,0,0),[]),History:[],Plugins:[],TaskActivity:new(now,[],0,0,0)));
        }
    }
    private static void Capture(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
