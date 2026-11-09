using System.Text.Json;
using CRS.Maintenance;
namespace CRS.Worker.Service;

public sealed partial class WorkerDataCleanup
{
    private const string AutomaticSid = "service:automatic-retention";
    private CancellationTokenSource? automaticCancellation;
    private DateTimeOffset automaticPausedUntil;
    private AutomaticCleanupSchedule? automaticSchedule;
    private DateTimeOffset automaticReadyAt;
    private sealed record AutomaticState(int Format,DateTimeOffset NextRunAt,Guid? LastOperationId,string LastState);
    private string AutomaticStatePath => Child("maintenance/automatic-cleanup.json");
    private bool HasManualPreview()=>operations.Values.Any(x=>!x.Automatic&&!x.BackgroundTask&&x.Status is{State:"Ready",CanExecute:true}&&x.Status.ExpiresAt>=Now);

    private AutomaticState ReadAutomaticState()
    {
        var path=AutomaticStatePath;
        if(!File.Exists(path))return new(1,Now,null,"NeverRun");
        if(new FileInfo(path).Length>4096)throw new InvalidDataException("自动清理记录超限。");
        var state=JsonSerializer.Deserialize<AutomaticState>(File.ReadAllBytes(path),Json)??throw new InvalidDataException("自动清理记录缺失。");
        if(state.Format!=1||state.NextRunAt==default||state.LastState is not ("NeverRun" or "Running" or "Completed" or "Interrupted" or "Unavailable" or "Paused"))
            throw new InvalidDataException("自动清理记录无效。");
        return state;
    }

    private void WriteAutomaticState(AutomaticState state)
    {
        Directory.CreateDirectory(Child("maintenance"));
        AtomicJson(AutomaticStatePath,state);
    }

    internal async Task RunAutomaticAsync(Action<string,string,string> diagnostic,CancellationToken token,AutomaticCleanupSchedule? schedule=null)
    {
        schedule??=AutomaticCleanupSchedule.Default;
        lock(stateGate){automaticSchedule=schedule;automaticReadyAt=Now+schedule.StartupDelay;}
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stopping.Token);
        await Task.Delay(schedule.StartupDelay,clock,linked.Token).ConfigureAwait(false);
        while(true)
        {
            linked.Token.ThrowIfCancellationRequested();
            try
            {
                var work=TryResumeQueuedTask(linked.Token,schedule)??TryStartAutomatic(linked.Token,schedule);
                if(work is not null)
                {
                    var result=await work.ConfigureAwait(false);
                    diagnostic("AutomaticDataCleanup","service",FormattableString.Invariant($"{result.State};Files={result.DeletedFiles};Bytes={result.DeletedBytes}"));
                }
            }
            catch(OperationCanceledException) when(linked.IsCancellationRequested){throw;}
            catch(Exception error) when(AutomaticFailure(error))
            {
                diagnostic("AutomaticDataCleanupUnavailable","service",error.GetType().Name);
                await Task.Delay(schedule.RetryInterval,clock,linked.Token).ConfigureAwait(false);
            }
            await Task.Delay(schedule.CheckInterval,clock,linked.Token).ConfigureAwait(false);
        }
    }

    private DataCleanupAutomaticPlan AutomaticPlan()
    {
        var schedule=automaticSchedule??AutomaticCleanupSchedule.Default;
        var state="NotStarted";DateTimeOffset? due=null;
        try
        {
            var saved=ReadAutomaticState();
            if(automaticSchedule is not null)
            {
                due=new[]{saved.NextRunAt,automaticReadyAt,automaticPausedUntil}.Max();
                state=active is{BackgroundTask:true,Task.IsCompleted:false}?"Running":due>Now?"Waiting":"Due";
                if(automaticPausedUntil>Now||saved.LastState=="Paused"&&saved.NextRunAt>Now)state="Paused";
            }
        }
        catch(Exception error) when(AutomaticFailure(error)){state="Unavailable";due=null;}
        return new(state,due,(int)schedule.StartupDelay.TotalSeconds,(int)schedule.CheckInterval.TotalSeconds,
            (int)schedule.SuccessInterval.TotalSeconds,(int)schedule.RetryInterval.TotalSeconds,14,1){KeepLatestUsableBackupOnly=true};
    }

    private static bool AutomaticFailure(Exception error)=>error is IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or FormatException or InvalidOperationException or OverflowException;

    internal Task<DataCleanupStatus>? TryStartAutomatic(CancellationToken token,AutomaticCleanupSchedule? schedule=null)
    {
        schedule??=AutomaticCleanupSchedule.Default;
        lock(stateGate)
        {
            token.ThrowIfCancellationRequested();
            if(automaticPausedUntil>Now)return null;
            if(active?.Task is{IsCompleted:false}||HasManualPreview())return null;
            if(RecentTasks().Any(x=>x.Task.State=="Queued"))return null;
            var state=ReadAutomaticState();if(state.NextRunAt>Now)return null;
            var current=new WorkerUpgradeJournal(root).Read();
            if(current is not null&&current.Stage is not ("Committed" or "RolledBack"))return null;
            return StartBackgroundTask(Guid.NewGuid(),AutomaticSid,true,token,schedule);
        }
    }

    private DataCleanupStatus RunBackgroundOperation(Operation operation,CancellationTokenSource cancellation,AutomaticCleanupSchedule schedule)
    {
        try
        {
            Update(operation,"Scanning","后台正在核对清理范围；前台无需等待。");CheckpointTask(operation);
            Scan(operation,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if(operation.Targets.Count==0)Update(operation,"Completed",operation.UnverifiedBackups==0
                ?"无需清理；当前资料和恢复副本保留。"
                :"没有可安全清理的项目。"+UnverifiedHint(operation));
            else
            {
                lock(stateGate)operation.Status=operation.Status with{State="Running",ExpiresAt=null,CanExecute=false,Message="后台正在按保留规则清理。"};
                WriteAudit(operation,operation.Id);
                Execute(operation,operation.Id,cancellation.Token);
            }
        }
        catch(OperationCanceledException){Update(operation,"Interrupted","后台清理已暂停；保留现场等待下次核对。");}
        catch(Exception error) when(AutomaticFailure(error)){Update(operation,"Unavailable","后台清理未完成，类别："+error.GetType().Name);}
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            lock(stateGate)operation.Status=operation.Status with{CountsComplete=operation.Status.State!="Running"};
            Update(operation,"Unavailable","后台清理异常结束，结果需核对。类别："+error.GetType().Name);
        }
        finally
        {
            try
            {
                CheckpointTask(operation);
                lock(stateGate)
                {
                    var saved=ReadAutomaticState();
                    var next=Now+(operation.Status.State=="Completed"?schedule.SuccessInterval:schedule.RetryInterval);
                    WriteAutomaticState(new(1,saved.NextRunAt>next?saved.NextRunAt:next,operation.Id,operation.Status.State));
                }
            }
            finally
            {
                lock(stateGate)
                {
                    if(ReferenceEquals(automaticCancellation,cancellation))automaticCancellation=null;
                    cancellation.Dispose();operation.Targets.Clear();operation.Retained.Clear();
                }
            }
        }
        return operation.Status;
    }

    /// <summary>维护入口暂停持久后台任务并保存结果，不取消旧版待确认的手动扫描或清理。</summary>
    internal async Task PauseAutomaticAsync(CancellationToken token,bool forUpgrade=true)
    {
        CancellationTokenSource? cancellation;Task? running;
        lock(stateGate)
        {
            if(forUpgrade&&(active is{Automatic:false,BackgroundTask:false,Task.IsCompleted:false}||HasManualPreview()))
                throw new InvalidOperationException("手动扫描、清理或待确认预览尚未结束。");
            automaticPausedUntil=Now+TimeSpan.FromMinutes(30);
            // 状态损坏时自动任务不会启动；仍允许管理员暂停并升级，不能因此锁死维护入口。
            try
            {
                var state=ReadAutomaticState();
                WriteAutomaticState(state with{NextRunAt=state.NextRunAt>automaticPausedUntil?state.NextRunAt:automaticPausedUntil,LastState="Paused"});
            }
            catch(Exception error) when(AutomaticFailure(error)){}
            cancellation=automaticCancellation;running=active is{BackgroundTask:true}?active.Task:null;
        }
        try{cancellation?.Cancel();}catch(ObjectDisposedException){}
        if(running is not null)await running.WaitAsync(TimeSpan.FromSeconds(10),token).ConfigureAwait(false);
    }

    internal Task YieldAutomaticAsync(CancellationToken token)
    {
        lock(stateGate)if(active?.Automatic!=true||active.Task?.IsCompleted!=false)return Task.CompletedTask;
        return PauseAutomaticAsync(token,forUpgrade:false);
    }
}

internal sealed record AutomaticCleanupSchedule(TimeSpan StartupDelay,TimeSpan CheckInterval,TimeSpan SuccessInterval,TimeSpan RetryInterval,double DutyCycle)
{
    internal static AutomaticCleanupSchedule Default{get;}=new(TimeSpan.FromMinutes(5),TimeSpan.FromSeconds(30),TimeSpan.FromDays(1),TimeSpan.FromHours(1),.1);
}
