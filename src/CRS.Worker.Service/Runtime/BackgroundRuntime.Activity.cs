using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using Quartz;
namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    private readonly Dictionary<JobKey,(string Execution,Guid? Request)> runningExecutions=[];
    private readonly Dictionary<string,(string Sid,DateTimeOffset Until,BackgroundTaskActivity Value)> activitySnapshots=[];
    // 首次手动运行不继承定时任务退避；已经尝试并返回RetryDeferred的同一请求仍遵守重试期限。
    private DateTimeOffset? ManualNotBefore(Guid request,TaskDefinition plan)=>
        repository.ReadCommandState(request)?.Code=="RetryDeferred"?plan.NotBefore:null;

    private BackgroundResponse TaskActivityPage(BackgroundRequest request,BackgroundCaller caller)
    {
        BackgroundResponse Error(string code)=>new(WorkerProtocol.Version,request.RequestId,code);
        if(request.PageSize is <1 or >10)return Error("InvalidRequest");
        var now=CRS.Timing.BeijingClock.Now;
        foreach(var expired in activitySnapshots.Where(x=>x.Value.Until<now).Select(x=>x.Key).ToArray())activitySnapshots.Remove(expired);
        string id;BackgroundTaskActivity snapshot;var offset=0;
        if(request.Cursor is {} cursor)
        {
            var parts=cursor.Split(':');
            if(parts.Length!=2||!int.TryParse(parts[1],out offset)||offset<0)return Error("InvalidRequest");
            id=parts[0];
            if(!activitySnapshots.TryGetValue(id,out var cached)||cached.Sid!=caller.Sid)return Error("SnapshotExpired");
            snapshot=cached.Value;
            if(offset>=snapshot.Rows.Count)return Error("InvalidRequest");
        }
        else
        {
            if(activitySnapshots.Count>=8)return Error("SnapshotLimit");
            snapshot=CaptureActivity(now);id=Guid.NewGuid().ToString("N");
            if(snapshot.Rows.Count>4096)return Error("ActivityLimit");
            activitySnapshots.Add(id,(caller.Sid,now.AddMinutes(1),snapshot));
        }
        var page=snapshot.Rows.Skip(offset).Take(request.PageSize).ToArray();
        var next=offset+page.Length<snapshot.Rows.Count?id+":"+(offset+page.Length):null;
        if(next is null)activitySnapshots.Remove(id);
        return new(WorkerProtocol.Version,request.RequestId,"Ok",NextCursor:next,TaskActivity:snapshot with{Rows=page});
    }
    private BackgroundTaskActivity CaptureActivity(DateTimeOffset now)
    {
        var plans=repository.Plans().ToDictionary(x=>(x.PluginId,x.JobId));
        string Name(string plugin,string job)=>plans.GetValueOrDefault((plugin,job))?.Name??job;
        var rows=new List<BackgroundTaskActivityRow>();
        foreach(var pair in runningExecutions)
        {
            var record=repository.FindExecution(pair.Value.Execution);
            if(record is null)continue;
            var progress=record.Progress?.LastOrDefault()?.Detail;
            var reason=progress?.Stage is "Cooldown" or "RateWait"?progress.Stage:"Executing";
            rows.Add(new(pair.Key.Group,pair.Key.Name,Name(pair.Key.Group,pair.Key.Name),"Running",reason,
                pair.Value.Request,record.ExecutionId,record.StartedUtc,progress?.Until));
        }
        var queuedRequests=new HashSet<Guid>();
        foreach(var plugin in waitingOrder)
        {
            var position=0;
            foreach(var key in plugin.Value)
            {
                var invocation=waiting[key].Invocation;
                Guid? request=Guid.TryParseExact(invocation.Request,"N",out var parsed)?parsed:null;
                if(request is {} manual)queuedRequests.Add(manual);
                rows.Add(new(key.Group,key.Name,Name(key.Group,key.Name),"Waiting",
                    pluginWriters[key.Group].CurrentCount==0?"PluginBusy":"AwaitingCapacity",request,QueuePosition:++position));
            }
        }
        foreach(var pending in repository.PendingRuns().Where(x=>!queuedRequests.Contains(x.RequestId)))
        {
            var plan=plans.GetValueOrDefault((pending.PluginId,pending.JobId));
            var deadline=plan is null?null:ManualNotBefore(pending.RequestId,plan);
            var reason=fault is not null?"ServiceFault":maintenance?"Maintenance":catalog.RequiresProcessRestart?"RestartRequired":
                plan is null||!HasHandler(plan)?"JobUnavailable":deadline>now?"RetryDeferred":
                pluginWriters.TryGetValue(pending.PluginId,out var writer)&&writer.CurrentCount==0?"PluginBusy":"AwaitingDispatch";
            rows.Add(new(pending.PluginId,pending.JobId,Name(pending.PluginId,pending.JobId),"Pending",reason,pending.RequestId,NotBefore:deadline));
        }
        return new(now,rows,rows.Count(x=>x.State=="Running"),rows.Count(x=>x.State=="Waiting"),rows.Count(x=>x.State=="Pending"));
    }
}
