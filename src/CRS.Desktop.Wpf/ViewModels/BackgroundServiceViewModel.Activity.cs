using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Worker.Ipc;
namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    public ObservableCollection<TaskActivityItem> RunningTasks {get;}=[];
    public ObservableCollection<TaskActivityItem> WaitingTasks {get;}=[];
    public ObservableCollection<TaskActivityItem> PendingTasks {get;}=[];
    [ObservableProperty] private string activityStatus="尚未查询当前任务。";
    private bool activityRefreshing;
    public async Task RefreshTaskActivityAsync()
    {
        if(activityRefreshing||lifetime.IsCancellationRequested)return;
        activityRefreshing=true;
        try
        {
            var rows=new List<BackgroundTaskActivityRow>();var cursors=new HashSet<string>();
            string? cursor=null;BackgroundTaskActivity? snapshot=null;
            do
            {
                var reply=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity",Cursor:cursor),lifetime);
                if(reply.Code=="UnknownCommand"||reply.Code=="Ok"&&reply.TaskActivity is null)
                {RunningTasks.Clear();WaitingTasks.Clear();PendingTasks.Clear();ActivityStatus="后台尚不支持当前任务明细，请更新配套 Worker；不能根据数量推断具体任务。";return;}
                if(reply.Code!="Ok"||reply.TaskActivity is not {} page)throw new InvalidOperationException("当前任务读取失败。");
                snapshot??=page;
                if(page.ObservedAt!=snapshot.ObservedAt||page.Running!=snapshot.Running||page.Waiting!=snapshot.Waiting||page.Pending!=snapshot.Pending)
                    throw new System.IO.InvalidDataException("任务快照不一致。");
                rows.AddRange(page.Rows);cursor=reply.NextCursor;
                if(rows.Count>4096||cursor is not null&&!cursors.Add(cursor))throw new System.IO.InvalidDataException("任务分页无效。");
            }while(cursor is not null);
            lifetime.ThrowIfCancellationRequested();
            if(rows.Count(x=>x.State=="Running")!=snapshot!.Running||rows.Count(x=>x.State=="Waiting")!=snapshot.Waiting||
                rows.Count(x=>x.State=="Pending")!=snapshot.Pending||rows.Any(x=>x.State is not ("Running" or "Waiting" or "Pending")))
                throw new System.IO.InvalidDataException("任务状态数量不匹配。");
            Replace(RunningTasks,"Running");Replace(WaitingTasks,"Waiting");Replace(PendingTasks,"Pending");
            ActivityStatus=$"执行中 {snapshot.Running} · 排队中 {snapshot.Waiting} · 待执行请求 {snapshot.Pending} · {snapshot.ObservedAt:yyyy-MM-dd HH:mm:ss}（北京时间）";
            void Replace(ObservableCollection<TaskActivityItem> target,string state)
            {target.Clear();foreach(var row in rows.Where(x=>x.State==state))target.Add(new(row));}
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception){ActivityStatus="当前任务未能更新；如有列表，内容为上次快照，请检查后台连接后刷新。";}
        finally{activityRefreshing=false;}
    }
}
public sealed record TaskActivityItem(BackgroundTaskActivityRow Row)
{
    public string ReasonText=>Row.Reason switch
    {
        "Executing"=>"正在执行", "Cooldown"=>"执行中，等待来源冷却", "RateWait"=>"执行中，等待来源访问额度",
        "PluginBusy"=>"等待同插件前序任务结束", "AwaitingCapacity"=>"等待可用执行名额", "RetryDeferred"=>"已尝试执行，等待重试时间",
        "AwaitingDispatch"=>"已受理，等待调度", "Maintenance"=>"后台维护中", "ServiceFault"=>"后台故障，需修复后恢复",
        "RestartRequired"=>"插件异常，等待后台重启", "JobUnavailable"=>"插件或任务不可用", _=>"状态待确认"
    };
    public string EarliestText=>Row.NotBefore?.ToString("yyyy-MM-dd HH:mm:ss")??"就绪后尽快执行";
}
