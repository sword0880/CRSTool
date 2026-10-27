using System.Text.Json;
using CRS.Maintenance;
namespace CRS.Worker.Service;

public sealed partial class WorkerDataCleanup
{
    private sealed record CleanupAudit(int Format,Guid Id,string Sid,Guid PreviewId,DataCleanupStatus Status);
    public DataCleanupStatus BeginPreview(Guid id,string sid,DataCleanupOptions options)
    {
        Validate(options);if(id==Guid.Empty||string.IsNullOrWhiteSpace(sid))throw new InvalidDataException("操作身份无效。");
        lock(stateGate)
        {
            if(operations.TryGetValue(id,out var existing)){if(existing.Options!=options)throw new InvalidDataException("操作编号已用于其他选项。");return Owned(existing,sid);}
            if(active?.Task is {IsCompleted:false})throw new InvalidOperationException("已有资料扫描或清理正在执行。");
            foreach(var prior in operations.Values.Where(x=>x.Status.State=="Ready")){prior.Targets.Clear();prior.Retained.Clear();prior.Status=prior.Status with{State="Expired",CanExecute=false,Message="已有新的扫描，旧预览失效。"};}
            foreach(var prior in operations.Keys.Take(Math.Max(0,operations.Count-16)).ToArray())operations.Remove(prior);
            var operation=new Operation(id,sid,options);operations.Add(id,operation);active=operation;
            operation.Task=Task.Run(()=>
            {
                try{Scan(operation,stopping.Token);}
                catch(OperationCanceledException){Update(operation,"Interrupted","扫描已中断，未清理任何资料。");}
                catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or OverflowException)
                {Update(operation,"Unavailable","无法完整核实清理范围；未清理任何资料。类别："+error.GetType().Name);}
            });
            return operation.Status;
        }
    }
    public DataCleanupStatus Status(Guid id,string sid)
    {
        lock(stateGate)
        {
            if(operations.TryGetValue(id,out var operation))return Owned(operation,sid);
            var path=Child("maintenance/cleanup/"+id.ToString("N")+".json");
            if(!File.Exists(path))throw new KeyNotFoundException("清理预览或任务不存在，请重新扫描。");
            if(new FileInfo(path).Length>256*1024)throw new InvalidDataException("清理记录超限。");
            var audit=JsonSerializer.Deserialize<CleanupAudit>(File.ReadAllBytes(path),Json)??throw new InvalidDataException("清理记录缺失。");
            if(audit.Format!=1||audit.Id!=id||audit.Sid!=sid)throw new UnauthorizedAccessException();
            return audit.Status.State is "Running"?audit.Status with{State="Interrupted",CanExecute=false,CountsComplete=false,Message="上次清理进程中断，删除数量未完整记录；重新扫描可继续清理已弃用的副本残留。"}:audit.Status;
        }
    }
    public DataCleanupStatus BeginExecute(Guid id,Guid previewId,string sid)
    {
        lock(stateGate)
        {
            if(operations.TryGetValue(id,out var existing))return Owned(existing,sid);
            var prior=Child("maintenance/cleanup/"+id.ToString("N")+".json");if(File.Exists(prior))return Status(id,sid);
            if(active?.Task is {IsCompleted:false})throw new InvalidOperationException("已有资料扫描或清理正在执行。");
            if(!operations.TryGetValue(previewId,out var preview)||preview.Sid!=sid)throw new UnauthorizedAccessException();
            if(preview.Status.State!="Ready"||preview.Status.ExpiresAt<Now||preview.Status.UpdatedAt>Now||preview.Targets.Count==0)throw new InvalidDataException("清理预览已失效或没有选中项，请重新扫描。");
            var operation=new Operation(id,sid,preview.Options){Targets=preview.Targets.ToList(),Retained=preview.Retained.ToList(),CurrentJournalHash=preview.CurrentJournalHash,
                Status=preview.Status with{OperationId=id,State="Running",CanExecute=false,UpdatedAt=Now,ExpiresAt=null,Message="清理已受理，正在重新核对文件。"}};
            WriteAudit(operation,previewId);preview.Status=preview.Status with{State="Expired",CanExecute=false};preview.Targets.Clear();preview.Retained.Clear();
            operations.Add(id,operation);active=operation;
            operation.Task=Task.Run(()=>Execute(operation,previewId,stopping.Token));return operation.Status;
        }
    }
    private DataCleanupStatus Owned(Operation operation,string sid)
    {
        if(operation.Sid!=sid)throw new UnauthorizedAccessException();
        // 任务退出前还需落盘最终结果，不能让新扫描与未结束的清理交错。
        return operation.Task is{IsCompleted:false}&&operation.Status.State is not ("Scanning" or "Running")
            ?operation.Status with{State=operation.Status.State=="Ready"?"Scanning":"Running",CanExecute=false,Message="正在完成核验或保存最终记录。"}:operation.Status;
    }
    private void Update(Operation operation,string state,string message)
    {lock(stateGate)operation.Status=operation.Status with{State=state,UpdatedAt=Now,CanExecute=false,Message=message};}
    private void WriteAudit(Operation operation,Guid previewId)
    {
        var directory=Child("maintenance/cleanup");Directory.CreateDirectory(directory);StorageSecurity.RejectLinks(directory);
        AtomicJson(Path.Combine(directory,operation.Id.ToString("N")+".json"),new CleanupAudit(1,operation.Id,operation.Sid,previewId,operation.Status));
    }
    private static void AtomicJson<T>(string path,T value)
    {
        StorageSecurity.RejectLinks(path);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(output,value);output.Flush(true);}File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public async ValueTask DisposeAsync()
    {stopping.Cancel();Task? task;lock(stateGate)task=active?.Task;if(task is not null)await task.ConfigureAwait(false);stopping.Dispose();}
}
