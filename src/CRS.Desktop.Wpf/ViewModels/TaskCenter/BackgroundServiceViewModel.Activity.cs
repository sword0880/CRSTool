// 后台当前任务的分页展示，完整核对同一快照和状态数量后更新列表，读取失败时明确保留上次快照。
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Worker.Ipc;
using CRS.DesktopClient.Services;
namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    public ObservableCollection<TaskActivityItem> RunningTasks {get;}=[];
    public ObservableCollection<TaskActivityItem> WaitingTasks {get;}=[];
    public ObservableCollection<TaskActivityItem> PendingTasks {get;}=[];
    [ObservableProperty] private string activityStatus="尚未查询当前任务。";
    private bool activityRefreshing;
    // 先校验整份分页的观察时点与状态数量，再替换列表；读取失败保留并标明旧快照。
    [RelayCommand]
    public async Task RefreshTaskActivityAsync()
    {
        if(activityRefreshing||lifetime.IsCancellationRequested)return;
        activityRefreshing=true;
        try
        {
            if(cleanup?.TasksAvailable==true)await cleanup.RefreshTasksAsync(lifetime);
            var snapshot=await WorkerTaskActivityReader.ReadAsync(client,lifetime);
            if(snapshot is null)
            {RunningTasks.Clear();WaitingTasks.Clear();PendingTasks.Clear();ActivityStatus="后台尚不支持当前任务明细，请更新配套 Worker；不能根据数量推断具体任务。";return;}
            var rows=snapshot.Rows;
            Replace(RunningTasks,"Running");Replace(WaitingTasks,"Waiting");Replace(PendingTasks,"Pending");
            if(CleanupTaskPresentation.Active(cleanup?.CurrentTask))
            {
                var item=CleanupTaskPresentation.Activity(cleanup!.CurrentTask!,cleanup.TaskStatusKnown);
                (item.Row.State=="Waiting"?WaitingTasks:RunningTasks).Add(item);
            }
            ActivityStatus=$"执行中 {RunningTasks.Count} · 排队中 {WaitingTasks.Count} · 待执行请求 {PendingTasks.Count} · {snapshot.ObservedAt:yyyy-MM-dd HH:mm:ss}（北京时间）";
            if(cleanup?.TasksAvailable==true&&!cleanup.TaskStatusKnown)ActivityStatus+=" · 清理状态未确认，清理任务如有显示则为上次快照。";
            void Replace(ObservableCollection<TaskActivityItem> target,string state)
            {target.Clear();foreach(var row in rows.Where(x=>x.State==state))target.Add(new(row));}
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception){ActivityStatus="当前任务未能更新；如有列表，内容为上次快照，请检查后台连接后刷新。";}
        finally{activityRefreshing=false;}
    }
}
public sealed record TaskActivityItem(BackgroundTaskActivityRow Row,string? ProgressText=null,DateTimeOffset? SubmittedAt=null)
{
    public DateTimeOffset? ActivityTime=>SubmittedAt??Row.StartedAt;
    public string ReasonText=>ProgressText??Row.Reason switch
    {
        "Executing"=>"正在执行", "Cooldown"=>"执行中，等待来源冷却", "RateWait"=>"执行中，等待来源访问额度",
        "PluginBusy"=>"等待同插件前序任务结束", "AwaitingCapacity"=>"等待可用执行名额", "RetryDeferred"=>"已尝试执行，等待重试时间",
        "AwaitingDispatch"=>"已受理，等待调度", "Maintenance"=>"后台维护中", "ServiceFault"=>"后台故障，需修复后恢复",
        "RestartRequired"=>"插件异常，等待后台重启", "JobUnavailable"=>"插件或任务不可用", _=>"状态待确认"
    };
    public string EarliestText=>Row.NotBefore?.ToString("yyyy-MM-dd HH:mm:ss")??"就绪后尽快执行";
}
