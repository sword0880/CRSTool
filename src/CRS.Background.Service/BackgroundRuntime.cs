using System.Collections.Specialized;
using CRS.Background.Abstractions;
using CRS.Background.Ipc;
using CRS.Background.PluginHost;
using Quartz;

namespace CRS.Background.Service;

/// <summary>服务独占调度与写入；命令、重建和任务开始按同一门闩排序。</summary>
public sealed class BackgroundRuntime(EncryptedTaskRepository repository, BackgroundPluginCatalog catalog, Action<string, string, string>? diagnostics = null) : IBackgroundControl, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<JobKey, CancellationTokenSource> running = [];
    private readonly Dictionary<JobKey, string> queued = [];
    private IScheduler? scheduler;
    private bool maintenance;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private static JobKey Key(TaskDefinition plan) => new(plan.JobId, plan.PluginId);
    private static TriggerKey CronKey(JobKey key) => new(key.Name + ".cron", key.Group);
    private static TriggerKey StartupKey(JobKey key) => new(key.Name + ".startup", key.Group);
    private static TriggerKey ManualKey(JobKey key) => new(key.Name + ".manual", key.Group);
    private static void Validate(string cron, string zone)
    {
        if (cron.Length > 120) throw new ArgumentException("Cron 不合法。");
        try { _ = new CronExpression(cron); }
        catch (FormatException) { throw new ArgumentException("Cron 不合法。"); }
        TimeZoneInfo.FindSystemTimeZoneById(zone);
    }
    public async Task StartAsync(CancellationToken token)
    {
        // 一进程只有此处创建一个 RAM 调度器；不使用任何明文 Quartz 持久表。
        scheduler = await QuartzSchedulerBuilder.Create().UseProperties(new NameValueCollection
            { { "quartz.scheduler.instanceName", "CRS.Background." + Guid.NewGuid().ToString("N") }, { "quartz.threadPool.maxConcurrency", "2" } }).BuildScheduler(token);
        scheduler.Context.Add("crs.background.runtime", this);
        foreach (var job in catalog.Jobs)
        {
            Validate(job.Descriptor.DefaultCron, job.Descriptor.TimeZoneId);
            repository.RegisterDefault(job.PluginId, job.Descriptor);
            await scheduler.AddJob(JobBuilder.Create<PluginJobDispatcher>().WithIdentity(job.Descriptor.JobId, job.PluginId).StoreDurably().Build(), cancellationToken: token);
        }
        foreach (var plan in repository.Plans().Where(HasHandler))
        {
            await ApplyAsync(plan, token);
            if (plan.Enabled)
                await scheduler.ScheduleJob(TriggerBuilder.Create().ForJob(Key(plan)).WithIdentity(StartupKey(Key(plan)))
                    .UsingJobData("revision", plan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .StartAt(DateTimeOffset.UtcNow.AddSeconds(2)).Build(), cancellationToken: token);
        }
        await scheduler.Start(token);
    }
    private bool HasHandler(TaskDefinition plan) => catalog.Jobs.Any(x => x.PluginId == plan.PluginId && x.Descriptor.JobId == plan.JobId);
    private async Task ApplyAsync(TaskDefinition plan, CancellationToken token)
    {
        Validate(plan.Cron, plan.TimeZoneId);
        var key = Key(plan);
        await scheduler!.UnscheduleJob(CronKey(key), token);
        await scheduler.UnscheduleJob(StartupKey(key), token);
        if (plan.Enabled)
            await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity(CronKey(key)).ForJob(key)
                .UsingJobData("revision", plan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithCronSchedule(plan.Cron, x => x.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId))
                    .WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing)).Build(), cancellationToken: token);
        repository.Reconciled(plan);
    }
    public async Task ReconcileAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (maintenance) return;
            foreach (var plan in repository.Plans().Where(x => x.NeedsReconcile && HasHandler(x)))
                await ApplyAsync(plan, token);
        }
        finally { gate.Release(); }
    }
    public async Task<BackgroundResponse> HandleAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token)
    {
        await gate.WaitAsync(token);
        string result = "Failed";
        try
        {
            BackgroundResponse Reply(string code) => new(1, request.RequestId, code);
            if (request.Command == "GetHealth") return new(1, request.RequestId, "Ok", new(maintenance ? "Maintenance" : repository.Plans().Any(x => x.NeedsReconcile || !HasHandler(x)) ? "Degraded" : "Running", started, DateTimeOffset.UtcNow, Environment.ProcessId));
            if (request.Command == "GetJobs") return new(1, request.RequestId, "Ok", Jobs: repository.Plans());
            if (request.Command == "GetHistory") return new(1, request.RequestId, "Ok", History: repository.History());
            if (!repository.ClaimCommand(caller.Sid, request.RequestId, request.Command)) { result = "ReplayRejected"; return Reply(result); }
            if (request.Command == "AuditDenied") { result = "Forbidden"; return Reply(result); }
            if (!caller.CanManage) { result = "Forbidden"; return Reply(result); }
            if (maintenance) { result = "Maintenance"; return Reply(result); }
            var plan = repository.Plans().SingleOrDefault(x => x.PluginId == request.PluginId && x.JobId == request.JobId);
            if (plan is null || !HasHandler(plan)) { result = "JobUnavailable"; return Reply(result); }
            var key = Key(plan);
            switch (request.Command)
            {
                case "RunNow":
                    if (running.ContainsKey(key) || queued.ContainsKey(key)) { result = "AlreadyRunning"; break; }
                    if (plan.NotBefore > DateTimeOffset.UtcNow) { result = "RetryDeferred"; break; }
                    var nonce = Guid.NewGuid().ToString("N"); queued.Add(key, nonce);
                    try
                    {
                        await scheduler!.ScheduleJob(TriggerBuilder.Create().WithIdentity(ManualKey(key)).ForJob(key).UsingJobData("manual", nonce).StartNow().Build(), cancellationToken: token);
                        result = "Accepted";
                    }
                    catch { queued.Remove(key); throw; }
                    break;
                case "CancelRun":
                    await CancelAsync(key, token); result = "Accepted"; break;
                case "Pause":
                case "Resume":
                case "ChangeSchedule":
                    if (request.ExpectedRevision is null) { result = "RevisionRequired"; break; }
                    var next = request.Command switch
                    {
                        "Pause" => plan with { Enabled = false },
                        "Resume" => plan with { Enabled = true },
                        _ => plan with { Cron = request.Cron ?? "", TimeZoneId = request.TimeZoneId ?? "" }
                    };
                    Validate(next.Cron, next.TimeZoneId);
                    repository.Save(next, request.ExpectedRevision.Value);
                    next = next with { Revision = plan.Revision + 1, NeedsReconcile = true };
                    if (!next.Enabled) await CancelAsync(key, token);
                    // 先提交权威配置；失败时保留 reconcile 标记，由下一轮及重启重新应用。
                    try { await ApplyAsync(next, token); result = "Ok"; }
                    catch (Exception) when (!token.IsCancellationRequested) { result = "AcceptedNeedsReconcile"; }
                    break;
                default: result = "UnknownCommand"; break;
            }
            return Reply(result);
        }
        catch (Exception error) when (error is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or InvalidOperationException)
        { result = "InvalidOrStaleRequest"; return new(1, request.RequestId, result); }
        finally
        {
            try
            {
                if (request.Command is not ("GetHealth" or "GetJobs" or "GetHistory"))
                    repository.Audit(caller.Sid, request.RequestId, request.Command, result);
            }
            finally { gate.Release(); }
        }
    }
    private async Task CancelAsync(JobKey key, CancellationToken token)
    {
        queued.Remove(key);
        await scheduler!.UnscheduleJob(ManualKey(key), token);
        if (running.TryGetValue(key, out var source)) source.Cancel();
    }
    internal async Task DispatchAsync(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var key = context.JobDetail.Key;
        TaskDefinition? plan = null; CancellationTokenSource? source = null; string? execution = null;
        await gate.WaitAsync(stopping.Token);
        try
        {
            if (maintenance || running.ContainsKey(key)) return;
            plan = repository.Plans().SingleOrDefault(x => x.PluginId == key.Group && x.JobId == key.Name);
            if (plan is null || !HasHandler(plan) || plan.NotBefore > DateTimeOffset.UtcNow) return;
            var manual = context.MergedJobDataMap.GetString("manual");
            if (manual is not null)
            {
                if (!queued.TryGetValue(key, out var nonce) || nonce != manual) return;
                queued.Remove(key);
            }
            else if (!plan.Enabled || context.MergedJobDataMap.GetString("revision") != plan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)) return;
            source = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken);
            source.CancelAfter(TimeSpan.FromMinutes(15));
            execution = repository.Begin(plan, context.ScheduledFireTimeUtc ?? DateTimeOffset.UtcNow);
            running.Add(key, source);
        }
        finally { gate.Release(); }
        if (execution is null || source is null || plan is null) return;
        BackgroundJobResult result;
        try
        {
            var handler = catalog.Jobs.Single(x => x.PluginId == plan.PluginId && x.Descriptor.JobId == plan.JobId);
            result = await handler.Session.ExecuteAsync(plan.JobId, source.Token);
            if (result.Status is not ("Succeeded" or "NotPublished" or "AccessBlocked" or "SourceSchemaChanged" or "RetryDeferred" or "Failed" or "Cancelled") || result.Checkpoint?.Length > 1024)
                result = new("Failed");
        }
        catch (OperationCanceledException) { result = new("Cancelled"); }
        catch (Exception) { result = new("Failed"); }
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            repository.End(execution, plan, result);
            diagnostics?.Invoke("ExecutionCompleted", plan.PluginId + ":" + plan.JobId, result.Status);
        }
        finally
        {
            running.Remove(key); source.Dispose(); gate.Release();
        }
    }
    public async Task EnterMaintenanceAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            maintenance = true;
            if (scheduler is null) return;
            await scheduler!.Standby(token);
            queued.Clear(); foreach (var source in running.Values) source.Cancel();
        }
        finally { gate.Release(); }
        // 不持锁等待工作结束，否则任务无法提交最终检查点。
        await scheduler!.Shutdown(waitForJobsToComplete: true, token);
    }
    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await EnterMaintenanceAsync(deadline.Token);
        stopping.Cancel(); stopping.Dispose(); gate.Dispose();
    }
}

/// <summary>所有后台任务共用分发器；Quartz 只存稳定 ID，单任务禁止并发。</summary>
[DisallowConcurrentExecution]
public sealed class PluginJobDispatcher : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await ((BackgroundRuntime)context.Scheduler.Context["crs.background.runtime"]!).DispatchAsync(context, cancellationToken);
}
