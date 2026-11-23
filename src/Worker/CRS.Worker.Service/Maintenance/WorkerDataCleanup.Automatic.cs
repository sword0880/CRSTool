using System.Text.Json;
using CRS.Maintenance;
using CRS.Timing;
using Quartz;
namespace CRS.Worker.Service;

public sealed partial class WorkerDataCleanup
{
    private const string AutomaticSid = "service:automatic-retention";
    private CancellationTokenSource? automaticCancellation;
    private DateTimeOffset automaticPausedUntil;
    private AutomaticCleanupSchedule? automaticSchedule;
    private bool scheduledCleanupDue;
    private DateTimeOffset automaticUnavailableUntil;
    private Action<Guid>? reportTerminalFailure;
    private sealed record AutomaticState(int Format,DateTimeOffset NextRunAt,Guid? LastOperationId,string LastState)
    {
        public string? Cron { get; init; }
        public int Failures { get; init; }
    }
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
        if (state is { Cron: null, LastState: "Completed" })
        {
            // 旧版完成时间加 24 小时保存。还原完成时间，再按新的每日计划计算。
            var schedule = automaticSchedule ?? AutomaticCleanupSchedule.Default;
            var completed = state.NextRunAt.AddDays(-1);
            state = state with { NextRunAt = schedule.NextAfter(completed) };
        }
        return state;
    }

    private void WriteAutomaticState(AutomaticState state)
    {
        Directory.CreateDirectory(Child("maintenance"));
        AtomicJson(AutomaticStatePath,state with { Cron = (automaticSchedule ?? AutomaticCleanupSchedule.Default).Cron });
    }

    internal void InitializeAutomaticSchedule(AutomaticCleanupSchedule schedule,Action<Guid>? reportTerminalFailure=null)
    {
        lock (stateGate) { automaticSchedule = schedule; this.reportTerminalFailure=reportTerminalFailure; }
    }

    internal async Task RunAutomaticOnceAsync(Action<string,string,string>? diagnostic,CancellationToken token,bool recoverOnly)
    {
        var schedule = automaticSchedule ?? AutomaticCleanupSchedule.Default;
        lock (stateGate) if (recoverOnly && automaticUnavailableUntil > Now) return;
        try
        {
            var work = TryResumeQueuedTask(token, schedule) ?? TryStartAutomatic(token, schedule, recoverOnly);
            if (work is null) return;
            var result = await work.ConfigureAwait(false);
            diagnostic?.Invoke("AutomaticDataCleanup","service",FormattableString.Invariant($"{result.State};Files={result.DeletedFiles};Bytes={result.DeletedBytes}"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (AutomaticFailure(error))
        {
            lock (stateGate) automaticUnavailableUntil = Now + schedule.RetryInterval;
            diagnostic?.Invoke("AutomaticDataCleanupUnavailable","service",error.GetType().Name);
        }
    }

    private DataCleanupAutomaticPlan AutomaticPlan()
    {
        var schedule=automaticSchedule??AutomaticCleanupSchedule.Default;
        var state="NotStarted";DateTimeOffset? due=null;
        var retentionDays = WorkerLogSettings.DefaultRetentionDays;
        var failures=0;
        try
        {
            retentionDays = ReadLogSettings().RetentionDays;
            var saved=ReadAutomaticState();
            failures=saved.Failures;
            if(automaticSchedule is not null)
            {
                due=saved.LastState=="NeverRun"&&!scheduledCleanupDue?schedule.NextAfter(Now)
                    : saved.NextRunAt>automaticPausedUntil?saved.NextRunAt:automaticPausedUntil;
                state=active is{BackgroundTask:true,Task.IsCompleted:false}?"Running":due>Now?"Waiting":"Due";
                if(automaticPausedUntil>Now||saved.LastState=="Paused"&&saved.NextRunAt>Now)state="Paused";
            }
        }
        catch(Exception error) when(AutomaticFailure(error)){state="Unavailable";due=null;}
        return new(state,due,0,30,86400,(int)schedule.RetryInterval.TotalSeconds,retentionDays,1)
        {KeepLatestUsableBackupOnly=true,Cron=schedule.Cron,TimeZoneId=BeijingClock.ZoneId,Failures=failures};
    }

    private static bool AutomaticFailure(Exception error)=>error is IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or FormatException or InvalidOperationException or ArgumentOutOfRangeException or OverflowException;

    internal Task<DataCleanupStatus>? TryStartAutomatic(CancellationToken token,AutomaticCleanupSchedule? schedule=null,bool recoverOnly=false)
    {
        schedule??=AutomaticCleanupSchedule.Default;
        lock(stateGate)
        {
            token.ThrowIfCancellationRequested();
            if (!recoverOnly) scheduledCleanupDue = true;
            if(automaticPausedUntil>Now)return null;
            if(active?.Task is{IsCompleted:false}||HasManualPreview())return null;
            if(RecentTasks().Any(x=>x.Task.State=="Queued"))return null;
            var state=ReadAutomaticState();if(state.NextRunAt>Now)return null;
            // 让 Quartz 优先执行正常的每日触发；协调只处理漏跑、受阻或失败后的到期项。
            if (recoverOnly && !scheduledCleanupDue && (state.LastState == "NeverRun"
                || state.LastState == "Completed" && Now - state.NextRunAt < TimeSpan.FromMinutes(1))) return null;
            var current=new WorkerUpgradeJournal(root).Read();
            if(current is not null&&current.Stage is not ("Committed" or "RolledBack"))return null;
            var run = StartBackgroundTask(Guid.NewGuid(),AutomaticSid,true,token,schedule);
            scheduledCleanupDue = false;
            return run;
        }
    }

    private DataCleanupStatus RunBackgroundOperation(Operation operation,CancellationTokenSource cancellation,AutomaticCleanupSchedule schedule)
    {
        try
        {
            lock(stateGate) operation.StartedAt=Now;
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
                    var failed=operation.Status.State!="Completed"&&!cancellation.IsCancellationRequested;
                    var exhausted=failed&&saved.Failures>=TaskRetryPolicy.MaximumRetries;
                    var next=operation.Status.State=="Completed"||exhausted?schedule.NextAfter(Now):Now+schedule.RetryInterval;
                    if (saved.LastState == "Paused" && saved.NextRunAt > next) next = saved.NextRunAt;
                    if(exhausted)reportTerminalFailure?.Invoke(operation.Id);
                    WriteAutomaticState(new(1,next,operation.Id,operation.Status.State){Failures=failed&&!exhausted?saved.Failures+1:0});
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
                var due = state.LastState == "NeverRun" && !scheduledCleanupDue
                    ? (automaticSchedule ?? AutomaticCleanupSchedule.Default).NextAfter(Now) : state.NextRunAt;
                WriteAutomaticState(state with{NextRunAt=due>automaticPausedUntil?due:automaticPausedUntil,LastState="Paused"});
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

internal sealed record AutomaticCleanupSchedule(string Cron,TimeSpan RetryInterval,double DutyCycle)
{
    internal static AutomaticCleanupSchedule Default { get; } = new("0 0 3 * * ?", TimeSpan.FromHours(1), .1);

    internal DateTimeOffset NextAfter(DateTimeOffset time)
    {
        var expression = new CronExpression(Cron, TimeZoneInfo.FindSystemTimeZoneById(BeijingClock.ZoneId));
        return BeijingClock.Normalize(expression.GetNextValidTimeAfter(time) ?? throw new InvalidOperationException("清理计划已没有后续触发时间。"));
    }
}
