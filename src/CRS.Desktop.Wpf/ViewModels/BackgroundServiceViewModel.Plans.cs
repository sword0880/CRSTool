using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Maintenance;
namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    private readonly ServicePlanItem maintenancePlan;
    public ObservableCollection<ServicePlanItem> PlanRows{get;}=[];
    [ObservableProperty]private ServicePlanItem? selectedPlan;
    partial void OnSelectedPlanChanged(ServicePlanItem? value){Selected=value?.Job;NotifyPlanCommands();}
    partial void OnUncertainRequestChanged(CRS.Worker.Ipc.BackgroundRequest? value)=>NotifyPlanCommands();
    private bool CanUseSelectedPlan()=>!Busy&&!lifetime.IsCancellationRequested&&UncertainRequest is null&&Selected is not null;
    private bool CanRunSelectedPlan()=>SelectedPlan?.IsMaintenance==true
        ?!Busy&&!lifetime.IsCancellationRequested&&UncertainRequest is null&&cleanup?.CurrentTask is null&&cleanup?.SubmitTaskCommand.CanExecute(null)==true
        :CanUseSelectedPlan();
    private void NotifyPlanCommands()
    {RunNowCommand.NotifyCanExecuteChanged();PauseCommand.NotifyCanExecuteChanged();ResumeCommand.NotifyCanExecuteChanged();SaveCommand.NotifyCanExecuteChanged();CancelRunCommand.NotifyCanExecuteChanged();}
    private void OnJobsChanged(object? sender,NotifyCollectionChangedEventArgs e)
    {
        if(e.Action==NotifyCollectionChangedAction.Reset){PlanRows.Clear();PlanRows.Add(maintenancePlan);return;}
        if(e.OldItems is not null)foreach(CRS.Worker.Abstractions.TaskDefinition job in e.OldItems)
        {var old=PlanRows.FirstOrDefault(x=>x.Job==job);if(old is not null)PlanRows.Remove(old);}
        if(e.NewItems is not null)
        {
            var index=e.NewStartingIndex+1;
            foreach(CRS.Worker.Abstractions.TaskDefinition job in e.NewItems)
            {
                var row=new ServicePlanItem(job);PlanRows.Insert(Math.Min(index++,PlanRows.Count),row);
                if(Selected==job)SelectedPlan=row;
            }
        }
    }
}

public sealed class ServicePlanItem:ObservableObject
{
    public CRS.Worker.Abstractions.TaskDefinition? Job{get;}
    public DataCleanupViewModel? Cleanup{get;}
    public bool IsMaintenance=>Job is null;
    public ServicePlanItem(CRS.Worker.Abstractions.TaskDefinition job)=>Job=job;
    internal ServicePlanItem(DataCleanupViewModel? cleanup)
    {
        Cleanup=cleanup;
        if(cleanup is not null)cleanup.PropertyChanged+=(_,_)=>OnPropertyChanged(string.Empty);
    }
    public string PluginId=>Job?.PluginId??DataCleanupTask.ServiceId;
    public string JobId=>Job?.JobId??DataCleanupTask.JobId;
    public string Name=>Job?.Name??"后台资料自动清理";
    public string Cron=>Job?.Cron??(Cleanup?.AutomaticCleanupPlan is {} plan
        ?plan.Cron??$"成功后 {TimeSpan.FromSeconds(plan.SuccessIntervalSeconds).TotalHours:N0} 小时":"保留周期待确认");
    public bool? Enabled=>Job?.Enabled??(Cleanup?.TaskStatusKnown==true&&Cleanup.AutomaticCleanupPlan is{State:not("Unavailable" or "NotStarted")}?true:null);
    public long? Revision=>Job?.Revision;
    public bool? NeedsReconcile=>Job?.NeedsReconcile;
    public string StateText => Job is { } job ? (job.PlanState switch
    {
        "Running"=>"执行中", "Unavailable"=>"任务不可用", "Disabled"=>"已暂停", "Reconcile"=>"等待计划生效",
        "Retry"=>"等待重试", "Deferred"=>"等待来源或资源", "Queued"=>"已排队",
        "Waiting" when job.LastResult is "Failed" or "AccessBlocked" or "SourceSchemaChanged"=>"上次执行失败",
        "Waiting"=>"等待执行",
        _=>!job.Enabled?"已暂停":job.NeedsReconcile?"等待计划生效":job.NotBefore is not null?"等待再次执行":"等待执行"
    }) : (Cleanup?.TaskStatusKnown==true ? Cleanup.AutomaticCleanupPlan?.State switch
    {
        "Running"=>"执行中", "Paused"=>"已暂停", "Unavailable"=>"任务不可用", "Due"=>"已到期", "NotStarted"=>"等待计划生效",
        "Waiting" when Cleanup.AutomaticCleanupPlan.Failures>0=>"等待重试",
        "Waiting" when Cleanup.LatestTaskResult?.State is "Unavailable" or "Interrupted"=>"上次未完成",
        "Waiting"=>"等待执行", _=>"状态未确认"
    } : "状态未确认");
    public DateTimeOffset? LastStartedAt=>Job?.LastStartedAt??(Cleanup?.TaskStatusKnown==true
        ?Cleanup.CurrentTask?.StartedAt??Cleanup.LatestTaskResult?.StartedAt??Cleanup.LatestTaskResult?.SubmittedAt:null);
    public string LastStartedText=>LastStartedAt is { } time?CRS.Timing.BeijingClock.Normalize(time).ToString("yyyy-MM-dd HH:mm:ss"):
        Job?.PlanState is not null||Cleanup?.TaskStatusKnown==true?"暂无记录":"未确认";
    public string NextDueText=>Job is {} job?(job.PlanState is "Disabled" or "Unavailable"||!job.Enabled&&job.PlanState is null?"—":
        (job.NotBefore??job.NextScheduledAt) is {} next?CRS.Timing.BeijingClock.Normalize(next).ToString("yyyy-MM-dd HH:mm:ss"):"按计划执行")
        :Cleanup?.TaskStatusKnown==true&&Cleanup.AutomaticCleanupPlan?.NextDueAt is {} due?due.ToString("yyyy-MM-dd HH:mm:ss"):"未确认";
}
