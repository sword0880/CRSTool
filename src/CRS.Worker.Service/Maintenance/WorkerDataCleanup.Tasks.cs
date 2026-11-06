using CRS.Maintenance;
namespace CRS.Worker.Service;

public sealed partial class WorkerDataCleanup
{
    public DataCleanupTaskOverview TaskOverview(string sid)
    {
        lock(stateGate)
        {
            var records=RecentTasks().Where(x=>TaskVisible(x,sid)).ToArray();
            var snapshots=records.Select(x=>active is{BackgroundTask:true}&&active.Id==x.Task.Id?TaskSnapshot(active,forReading:true):x.Task).ToArray();
            bool Finished(DataCleanupTask task)=>!TaskActive(task)&&!(active?.Id==task.Id&&active.Task is{IsCompleted:false});
            var current=snapshots.FirstOrDefault(TaskActive);var result=snapshots.FirstOrDefault(Finished);
            var notification=result is not null&&!records.Single(x=>x.Task.Id==result.Id).AcknowledgedBy.Contains(sid)?result:null;
            var available=active?.Task is not{IsCompleted:false}&&!HasManualPreview()&&current is null;
            return new(current,result,notification,available){AutomaticPlan=AutomaticPlan()};
        }
    }
    public DataCleanupTaskOverview SubmitTask(Guid requestId,string sid)=>SubmitTask(requestId,sid,AutomaticCleanupSchedule.Default);
    internal DataCleanupTaskOverview SubmitTask(Guid requestId,string sid,AutomaticCleanupSchedule schedule)
    {
        if(requestId==Guid.Empty||string.IsNullOrWhiteSpace(sid))throw new InvalidDataException("清理任务身份无效。");
        lock(stateGate)
        {
            if(RetryStoredSubmission(requestId,sid,schedule))return TaskOverview(sid);
            if(active is{BackgroundTask:true,Task.IsCompleted:false})
            {if(active.Sid!=sid&&active.Sid!=AutomaticSid)throw new InvalidOperationException("已有其他管理员的清理任务。");return TaskOverview(sid);}
            var pending=RecentTasks().FirstOrDefault(x=>TaskActive(x.Task));
            if(pending is not null){if(!TaskVisible(pending,sid))throw new InvalidOperationException("已有待执行清理任务。");return TaskOverview(sid);}
            if(active?.Task is{IsCompleted:false}||HasManualPreview())throw new InvalidOperationException("已有手动扫描或待确认预览。");
            _=StartBackgroundTask(requestId,sid,false,stopping.Token,schedule);
            return TaskOverview(sid);
        }
    }
    private bool RetryStoredSubmission(Guid id,string sid,AutomaticCleanupSchedule schedule)
    {
        if(!File.Exists(TaskPath(id)))return false;
        var record=ReadTaskRecord(id);if(!TaskVisible(record,sid))throw new UnauthorizedAccessException();
        // 若进程在写任务文件后、更新索引前退出，原编号重试须补齐受理，不能返回空结果。
        if(record.Task.State=="Queued"&&!ReadTaskIndex().Ids.Contains(id)&&active?.Task is not{IsCompleted:false}
            &&!HasManualPreview()&&!RecentTasks().Any(x=>TaskActive(x.Task)))
            _=StartBackgroundTask(id,record.Sid,record.Task.Origin=="Automatic",stopping.Token,schedule);
        return true;
    }
    public DataCleanupTaskOverview AcknowledgeTask(Guid id,string sid)
    {
        lock(stateGate)
        {
            var selected=ReadTaskRecord(id);
            if(!TaskVisible(selected,sid))throw new UnauthorizedAccessException();
            if(TaskActive(selected.Task)||active?.Id==id&&active.Task is{IsCompleted:false})throw new InvalidOperationException("任务尚未结束。");
            var older=false;
            foreach(var record in RecentTasks())
            {
                older|=record.Task.Id==id;
                if(!older||!TaskVisible(record,sid)||TaskActive(record.Task)||record.AcknowledgedBy.Contains(sid))continue;
                if(record.AcknowledgedBy.Length>=64)throw new InvalidDataException("清理通知确认身份超限。");
                AtomicJson(TaskPath(record.Task.Id),record with{AcknowledgedBy=[..record.AcknowledgedBy,sid]});
            }
            return TaskOverview(sid);
        }
    }
    private Task<DataCleanupStatus> StartBackgroundTask(Guid id,string sid,bool automatic,CancellationToken token,AutomaticCleanupSchedule schedule)
    {
        var journal=new WorkerUpgradeJournal(root).Read();
        if(journal is not null&&journal.Stage is not ("Committed" or "RolledBack"))throw new InvalidOperationException("后台正在升级维护。");
        _=ReadAutomaticState();
        var operation=new Operation(id,sid,new()){Automatic=automatic,BackgroundTask=true,SubmittedAt=Now};
        if(File.Exists(TaskPath(id)))operation.SubmittedAt=ReadTaskRecord(id).Task.SubmittedAt;
        operation.Status=operation.Status with{State="Queued",UpdatedAt=Now,Message="清理任务已提交后台，可以关闭本页。",Progress=new("Queued",null)};
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,stopping.Token);
        operation.Pacer=new(cancellation.Token,schedule.DutyCycle);
        try{RegisterTask(operation);WriteAutomaticState(new(1,Now+schedule.RetryInterval,id,"Running"));}
        catch{cancellation.Dispose();throw;}
        automaticCancellation=cancellation;active=operation;
        foreach(var old in operations.Where(x=>x.Value.BackgroundTask&&x.Value.Task?.IsCompleted!=false).Select(x=>x.Key).ToArray())operations.Remove(old);
        operations.Add(id,operation);var run=Task.Run(()=>RunBackgroundOperation(operation,cancellation,schedule),CancellationToken.None);
        operation.Task=run;return run;
    }
    private Task<DataCleanupStatus>? TryResumeQueuedTask(CancellationToken token,AutomaticCleanupSchedule schedule)
    {
        lock(stateGate)
        {
            if(automaticPausedUntil>Now||active?.Task is{IsCompleted:false}||HasManualPreview())return null;
            var pending=RecentTasks().FirstOrDefault(x=>x.Task.State=="Queued");if(pending is null)return null;
            var state=ReadAutomaticState();if(state.LastState=="Paused"&&state.NextRunAt>Now)return null;
            return StartBackgroundTask(pending.Task.Id,pending.Sid,pending.Task.Origin=="Automatic",token,schedule);
        }
    }
}
