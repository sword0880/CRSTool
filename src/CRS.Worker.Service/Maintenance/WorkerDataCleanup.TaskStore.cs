using System.Text.Json;
using CRS.Maintenance;
namespace CRS.Worker.Service;

public sealed partial class WorkerDataCleanup
{
    private sealed record TaskRecord(int Format,string Sid,DataCleanupTask Task,string[] AcknowledgedBy);
    private sealed record TaskIndex(int Format,Guid[] Ids);
    private string TaskPath(Guid id)=>Child("maintenance/cleanup-tasks/"+id.ToString("N")+".json");
    private string TaskIndexPath=>Child("maintenance/cleanup-tasks/index.json");
    private static bool TaskActive(DataCleanupTask task)=>task.State is "Queued" or "Scanning" or "Running";
    private static bool TaskVisible(TaskRecord record,string sid)=>record.Sid==sid||record.Sid==AutomaticSid;
    private TaskIndex ReadTaskIndex()
    {
        if(!File.Exists(TaskIndexPath))return new(1,[]);
        if(new FileInfo(TaskIndexPath).Length>4096)throw new InvalidDataException("清理任务索引超限。");
        var index=JsonSerializer.Deserialize<TaskIndex>(File.ReadAllBytes(TaskIndexPath),Json)??throw new InvalidDataException("清理任务索引缺失。");
        if(index.Format!=1||index.Ids.Length>32||index.Ids.Any(x=>x==Guid.Empty)||index.Ids.Distinct().Count()!=index.Ids.Length)
            throw new InvalidDataException("清理任务索引无效。");
        return index;
    }
    private TaskRecord ReadTaskRecord(Guid id)
    {
        var path=TaskPath(id);
        if(new FileInfo(path).Length>32768)throw new InvalidDataException("清理任务记录超限。");
        var record=JsonSerializer.Deserialize<TaskRecord>(File.ReadAllBytes(path),Json)??throw new InvalidDataException("清理任务记录缺失。");
        var task=record.Task;
        if(record.Format!=1||task.Id!=id||string.IsNullOrWhiteSpace(record.Sid)||record.Sid.Length>184||record.AcknowledgedBy.Length>64
            ||task.Origin is not ("Manual" or "Automatic")||task.State is not ("Queued" or "Scanning" or "Running" or "Completed" or "Interrupted" or "Unavailable")
            ||task.DeletedBytes<0||task.DeletedFiles<0||task.SubmittedAt==default||task.UpdatedAt==default||TaskActive(task)==(task.FinishedAt is not null))
            throw new InvalidDataException("清理任务记录无效。");
        return record;
    }
    private List<TaskRecord> RecentTasks()=>ReadTaskIndex().Ids.Select(ReadTaskRecord).ToList();
    private DataCleanupTask TaskSnapshot(Operation operation,bool forReading=false)
    {
        var status=operation.Status;var state=status.State=="Ready"?"Scanning":status.State;
        var message=status.Message;
        if(forReading&&operation.Task is{IsCompleted:false}&&state is not ("Queued" or "Scanning" or "Running"))
        {state="Running";message="后台正在保存清理结果。";}
        var terminal=state is "Completed" or "Interrupted" or "Unavailable";
        return new(operation.Id,operation.Automatic?"Automatic":"Manual",state,operation.SubmittedAt,status.UpdatedAt,
            terminal?status.UpdatedAt:null,status.DeletedBytes,status.DeletedFiles,status.CountsComplete,message,status.Progress);
    }
    private void RegisterTask(Operation operation)
    {
        var index=ReadTaskIndex();Directory.CreateDirectory(Child("maintenance/cleanup-tasks"));
        if(!File.Exists(TaskPath(operation.Id)))AtomicJson(TaskPath(operation.Id),new TaskRecord(1,operation.Sid,TaskSnapshot(operation),[]));
        AtomicJson(TaskIndexPath,new TaskIndex(1,new[]{operation.Id}.Concat(index.Ids.Where(x=>x!=operation.Id)).Take(32).ToArray()));
    }
    private void CheckpointTask(Operation operation)
    {
        if(!operation.BackgroundTask)return;
        lock(stateGate)
        {
            var record=ReadTaskRecord(operation.Id);
            AtomicJson(TaskPath(operation.Id),record with{Task=TaskSnapshot(operation)});
        }
    }
    /// <summary>仅持有服务所有权租约的组合根调用；构造器和只读审计绝不改写在途任务。</summary>
    internal void InitializeTasks(Action<string,string,string>? diagnostic=null)
    {
        try{RecoverCleanupTasks();}
        catch(Exception error) when(AutomaticFailure(error)){diagnostic?.Invoke("DataCleanupTasksUnavailable","service",error.GetType().Name);}
    }
    private void RecoverCleanupTasks()
    {
        foreach(var record in RecentTasks().Where(x=>x.Task.State is "Scanning" or "Running"))
        {
            var task=record.Task with{State="Interrupted",FinishedAt=Now,UpdatedAt=Now,
                CountsComplete=record.Task.State=="Scanning"&&record.Task.CountsComplete,
                Message="上次后台进程中断，清理未确认全部完成；后台会按保留规则重新核对。"};
            AtomicJson(TaskPath(task.Id),record with{Task=task});
        }
    }
}
