using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages;
using System.Windows;
using System.Windows.Controls;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

/// <summary>真实 WPF 页面验证执行记录深链接与异常筛选控件，不连接或修改正式 Worker。</summary>
internal static class TaskCenterDeepLinkChecks
{
    public static void Run()
    {
        Exception? error=null;
        using var stop=new CancellationTokenSource();var control=new Control();
        var pipe="CRS.History.Manual."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        var thread=new Thread(() =>
        {
            try
            {
                var app=new Application();
                var model=new BackgroundServiceViewModel(stop.Token,new(pipe));
                var page=new BackgroundServicePage(model);
                var plans=(DataGrid)page.FindName("PlansGrid");
                if(!plans.Columns.OfType<DataGridTextColumn>().Any(x=>x.Header?.ToString()=="任务编号（英文）" && x.Binding is System.Windows.Data.Binding {Path.Path:"JobId"}))
                    throw new InvalidOperationException("服务计划缺少英文任务编号。");
                var window=new Window{Content=page,Width=1200,Height=780,ShowActivated=false,ShowInTaskbar=false};
                var tabs=page.FindName("TaskCenterTabs") as TabControl
                    ?? throw new InvalidOperationException("任务中心缺少明确命名的TabControl。");
                // 尚未Loaded的TabControl允许 SelectedIndex=-1，呈现后才会自动选中首个页签。
                if(tabs.SelectedIndex is not (-1 or 0))throw new InvalidOperationException("任务中心初始页签异常。");
                page.ShowCurrentTasks();
                if(tabs.SelectedIndex!=2||(tabs.SelectedItem as TabItem)?.Header?.ToString()!="当前任务"||
                    page.FindName("RunningTasksGrid") is not DataGrid||page.FindName("WaitingTasksGrid") is not DataGrid||page.FindName("PendingTasksGrid") is not DataGrid)
                    throw new InvalidOperationException("当前任务缺少运行、排队和待执行三张表。");
                page.ShowRecentExecutions();
                if(tabs.SelectedIndex!=1 || (tabs.SelectedItem as TabItem)?.Header?.ToString()!="执行记录")
                    throw new InvalidOperationException("右下角直达未选中执行记录页签。");
                if(page.FindName("HistoryStartDatePicker") is not DatePicker {SelectedDateFormat:DatePickerFormat.Short}||
                    page.FindName("HistoryEndDatePicker") is not DatePicker {SelectedDateFormat:DatePickerFormat.Short}||page.FindName("MoreRecordsButton") is not null)
                    throw new InvalidOperationException("执行记录缺少起止日期查询入口。");
                if(Find<Button>(page).Any(x=>x.Content?.ToString()=="今天"))throw new InvalidOperationException("执行记录仍显示今天按钮。");
                var check=page.FindName("OnlyIssuesCheckBox") as CheckBox
                    ?? throw new InvalidOperationException("执行记录缺少异常筛选控件。");
                model.OnlyIssues=true;
                page.Measure(new(1200,780));page.Arrange(new(0,0,1200,780));page.UpdateLayout();
                if(check.IsChecked!=true)throw new InvalidOperationException("异常筛选未绑定到任务模型。");
                model.OnlyIssues=false;
                window.UpdateLayout();
                if(check.IsChecked!=false)throw new InvalidOperationException("恢复全部记录未清除勾选。");
                window.Show();
                page.ShowCurrentTasks();page.ShowRecentExecutions();
                // 泵送真实 Dispatcher 超过原3秒周期，覆盖 Loaded 和页签切换后的计时器行为。
                var frame=new System.Windows.Threading.DispatcherFrame();
                var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(3500)};
                timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                if(control.HistoryReads!=0)throw new InvalidOperationException("进入执行记录或等待3秒后仍自动查询。");
                window.Close();
                foreach(var status in new[]{"RetryDeferred","Failed","AccessBlocked","SourceSchemaChanged","Interrupted","Rejected"})
                    if(!BackgroundServiceViewModel.IsIssueResult(status))
                        throw new InvalidOperationException("异常状态丢失："+status);
                foreach(var status in new[]{"Succeeded","NotPublished","Running","Cancelled"})
                    if(BackgroundServiceViewModel.IsIssueResult(status))
                        throw new InvalidOperationException("普通/主动取消状态误报为异常："+status);
                Console.WriteLine("PASS 任务中心底栏深链接：起止日期、移除今天、页签切换和3秒后不自动查询、异常条件绑定。");
            }
            catch(Exception failure){error=failure;}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();thread.Join();stop.Cancel();try{server.GetAwaiter().GetResult();}catch(OperationCanceledException){}
        if(error is not null)throw error;
    }
    private sealed class Control:IBackgroundControl
    {
        internal int HistoryReads;
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if(request.Command=="GetHistoryByDate")Interlocked.Increment(ref HistoryReads);
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownCommand"));
        }
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);
            if(child is T match)yield return match;
            foreach(var descendant in Find<T>(child))yield return descendant;
        }
    }
}
