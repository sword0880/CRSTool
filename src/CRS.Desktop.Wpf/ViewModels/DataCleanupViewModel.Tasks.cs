using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;
using CRS.Maintenance;
namespace CRS.DesktopClient.ViewModels;

public sealed partial class DataCleanupViewModel
{
    private IDataCleanupTaskGateway? Tasks=>gateway as IDataCleanupTaskGateway;
    private CancellationTokenSource? taskMonitoring;
    private Task? taskMonitor;
    private Guid? taskSubmission;
    private DataCleanupTask? notification;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(SubmitTaskCommand)),NotifyCanExecuteChangedFor(nameof(RefreshTaskCommand)),NotifyCanExecuteChangedFor(nameof(DismissTaskNotificationCommand))]
    private bool taskRequestBusy;
    [ObservableProperty]private string taskMessage="提交后由后台核验并清理，完成后在主窗口提醒，可以关闭设置。";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(DismissTaskNotificationCommand))]private bool hasTaskNotification;
    [ObservableProperty]private string taskNotificationTitle="";
    [ObservableProperty]private string taskNotificationMessage="";
    [ObservableProperty]private DataCleanupTask? currentTask;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(AutomaticPlanStatus)),NotifyPropertyChangedFor(nameof(AutomaticPlanNext))]private bool taskStatusKnown;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(AutomaticPlanStatus)),NotifyPropertyChangedFor(nameof(AutomaticPlanPolicy)),NotifyPropertyChangedFor(nameof(AutomaticPlanNext))]
    private DataCleanupAutomaticPlan? automaticCleanupPlan;
    public string AutomaticPlanPolicy=>AutomaticCleanupPlan is {} plan
        ?$"成功后 {TimeSpan.FromSeconds(plan.SuccessIntervalSeconds).TotalHours:N0} 小时再次到期；未完成时 {TimeSpan.FromSeconds(plan.RetryIntervalSeconds).TotalMinutes:N0} 分钟后重试。日志保留 {plan.LogRetentionDays} 天，保留当前快照及 {plan.HistoricalBackupsToKeep} 份有效历史副本。"
        :"后台按保留规则执行；尚未取得计划详情。";
    public string AutomaticPlanStatus=>!TaskStatusKnown?"状态未确认":AutomaticCleanupPlan?.State switch
    {"Running"=>"清理执行中","Waiting"=>"已启用，等待到期","Due"=>"已到期，等待可用时机","Paused"=>"维护暂停中，到期后恢复","NotStarted"=>"自动清理循环尚未启动","Unavailable"=>"计划状态不可用，请检查后台",_=>"当前后台未提供维护计划摘要，请更新配套后台。"};
    public string AutomaticPlanNext=>TaskStatusKnown&&AutomaticCleanupPlan?.NextDueAt is {} due
        ?$"下次到期：{due:yyyy-MM-dd HH:mm:ss}（北京时间）；实际执行需等待当前清理或维护结束。":"下次到期时间未确认。";
    public bool TasksAvailable=>Tasks?.CanExecute==true;
    public string TaskAccessMessage=>TasksAvailable?"后台固定保留当前快照和最近一份有效历史副本，日志至少保留14天；当前数据库、密钥和插件原件保留。":"需要管理员权限及支持后台清理任务的配套服务。";
    private bool CanSubmitTask()=>TasksAvailable&&!TaskRequestBusy;
    private bool CanDismissTaskNotification()=>CanSubmitTask()&&HasTaskNotification;
    [RelayCommand(CanExecute=nameof(CanSubmitTask))]
    private async Task SubmitTaskAsync()
    {
        if(!CanSubmitTask())return;
        taskSubmission??=Guid.NewGuid();var id=taskSubmission.Value;
        await TaskRequestAsync(async token=>
        {
            var overview=await Tasks!.SubmitAsync(id,token);taskSubmission=null;
            TaskMessage=overview.Current is not null?"任务已交给后台执行，完成后会在主窗口提醒，可以关闭设置。":"后台已受理，结果将通过主窗口提醒。";
            return overview;
        },CancellationToken.None,TimeSpan.FromSeconds(35));
    }
    [RelayCommand(CanExecute=nameof(CanSubmitTask))]
    private Task RefreshTaskAsync()=>RefreshTasksAsync(CancellationToken.None);
    internal Task RefreshTasksAsync(CancellationToken token)=>TaskRequestAsync(cancellation=>Tasks!.LatestAsync(cancellation),token,TimeSpan.FromSeconds(6));
    [RelayCommand(CanExecute=nameof(CanDismissTaskNotification))]
    private async Task DismissTaskNotificationAsync()
    {
        if(notification is not {} displayed)return;
        await TaskRequestAsync(token=>Tasks!.AcknowledgeAsync(displayed.Id,token),CancellationToken.None,TimeSpan.FromSeconds(10));
    }
    private async Task TaskRequestAsync(Func<CancellationToken,Task<DataCleanupTaskOverview>> request,CancellationToken token,TimeSpan timeout)
    {
        if(!CanSubmitTask())return;
        TaskRequestBusy=true;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(timeout);
        try{var overview=await request(deadline.Token);deadline.Token.ThrowIfCancellationRequested();ApplyTaskOverview(overview);}
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
        catch(Exception error){TaskStatusKnown=false;TaskMessage="暂未确认后台任务状态；重试会核对现有任务，不会重复创建。 "+error.Message;}
        finally{TaskRequestBusy=false;}
    }
    internal void ApplyTaskOverview(DataCleanupTaskOverview overview)
    {
        CurrentTask=overview.Current;AutomaticCleanupPlan=overview.AutomaticPlan;TaskStatusKnown=true;
        notification=overview.Notification;HasTaskNotification=notification is not null;
        if(notification is null){TaskNotificationTitle="";TaskNotificationMessage="";return;}
        TaskNotificationTitle=notification.State=="Completed"&&notification.CountsComplete?"后台资料清理完成":"后台资料清理未全部完成";
        TaskNotificationMessage=notification.CountsComplete
            ?$"已清理 {FormatBytes(notification.DeletedBytes)} / {notification.DeletedFiles:N0} 个文件。 "+notification.Message
            :"删除数量未完整记录，不能视为全部完成。 "+notification.Message;
    }
    public void StartTaskMonitoring(CancellationToken token)
    {
        if(!TasksAvailable||taskMonitor is{IsCompleted:false})return;
        taskMonitoring=CancellationTokenSource.CreateLinkedTokenSource(token);taskMonitor=MonitorTasksAsync(taskMonitoring);
    }
    private async Task MonitorTasksAsync(CancellationTokenSource stopping)
    {
        using(stopping)
        try
        {
            while(true)
            {
                await RefreshTasksAsync(stopping.Token);
                await Task.Delay(TimeSpan.FromSeconds(3),stopping.Token);
            }
        }
        catch(OperationCanceledException) when(stopping.IsCancellationRequested){}
    }
    public void StopTaskMonitoring(){try{taskMonitoring?.Cancel();}catch(ObjectDisposedException){}}
}
