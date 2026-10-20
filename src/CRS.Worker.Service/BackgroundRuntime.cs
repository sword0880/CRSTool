using System.Collections.Specialized;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using System.Data.Common;
using Quartz;

namespace CRS.Worker.Service;

/// <summary>加密仓储是唯一权威；内存触发器、命令恢复和开始执行按同一门闩排序。</summary>
public sealed partial class BackgroundRuntime(ITaskRepository repository, BackgroundPluginCatalog catalog,
    Action<string, string, string>? diagnostics = null, Action? onUnresponsive = null,
    TimeSpan? executionTimeout = null, WorkerExecutionSettings? executionSettings = null,WorkerNotificationGateway? notifications=null,SourceBudgetRegistry? sources=null) : IBackgroundControl, IAsyncDisposable
{
    private readonly WorkerExecutionSettings settings = (executionSettings ?? new()).Validate();
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stopping = new();
    private readonly CancellationTokenSource queryStopping = new();
    private readonly HashSet<TaskCompletionSource> activeQueries = [];
    private readonly HashSet<string> queryingPlugins = new(StringComparer.Ordinal);
    private readonly Dictionary<JobKey, CancellationTokenSource> running = [];
    private readonly Dictionary<JobKey, string> queued = [];
    private readonly Dictionary<string, SemaphoreSlim> pluginWriters = [];
    private IScheduler? scheduler;
    private bool maintenance;
    private string? fault;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private static JobKey Key(TaskDefinition plan) => new(plan.JobId, plan.PluginId);
    private static TriggerKey TriggerKey(JobKey key, string kind) => new(key.Name + "." + kind, key.Group);
    private static bool StorageError(Exception error) => error is DbException or IOException or UnauthorizedAccessException or ObjectDisposedException;
    private static void Validate(string cron, string zone)
    {
        if (cron.Length > 120) throw new ArgumentException("Cron 不合法。");
        try { _ = new CronExpression(cron); } catch (FormatException) { throw new ArgumentException("Cron 不合法。"); }
        TimeZoneInfo.FindSystemTimeZoneById(zone);
    }
    private bool HasHandler(TaskDefinition plan) => catalog.Jobs.Any(x => x.PluginId == plan.PluginId && x.Descriptor.JobId == plan.JobId);
    private async Task TripFaultAsync(string state)
    {
        fault ??= state;
        foreach (var source in running.Values) source.Cancel();
        if (scheduler is not null) await scheduler.Standby(CancellationToken.None);
        diagnostics?.Invoke("HostFault", "service", state);
    }
    public async Task StartAsync(CancellationToken token)
    {
        scheduler = await QuartzSchedulerBuilder.Create().UseProperties(new NameValueCollection
            { { "quartz.scheduler.instanceName", "CRS.Worker." + Guid.NewGuid().ToString("N") }, { "quartz.threadPool.maxConcurrency", settings.EffectiveMaxConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture) } }).BuildScheduler(token);
        scheduler.Context.Add("crs.worker.runtime", this);
        foreach (var group in catalog.Jobs.GroupBy(x => x.PluginId).Select(x => x.ToArray()).ToArray())
        {
            try { foreach (var job in group) Validate(job.Descriptor.DefaultCron, job.Descriptor.TimeZoneId); }
            catch (Exception error) when (error is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { catalog.Quarantine(group[0].PluginId, "InvalidSchedule"); continue; }
            foreach (var job in group)
            {
            repository.RegisterDefault(job.PluginId, job.Descriptor);
            if (!pluginWriters.ContainsKey(job.PluginId))
            {
                var limit = Math.Min(job.MaximumConcurrency, Math.Max(1, settings.EffectiveMaxConcurrency / 2));
                pluginWriters.Add(job.PluginId, new(limit, limit));
                pluginLimits.Add(job.PluginId, limit);
                waitingOrder.Add(job.PluginId, new());
            }
            await scheduler.AddJob(JobBuilder.Create<PluginJobDispatcher>().WithIdentity(job.Descriptor.JobId, job.PluginId).StoreDurably().Build(), cancellationToken: token);
            }
        }
        foreach (var plan in repository.Plans().Where(HasHandler))
        {
            await ApplyAsync(plan, token);
            var descriptor = catalog.Jobs.Single(x => x.PluginId == plan.PluginId && x.Descriptor.JobId == plan.JobId).Descriptor;
            // 启动补偿由插件显式声明，禁止所有普通定时任务一律启动执行。
            if (plan.Enabled && descriptor.RunOnStartup && plan.NotBefore is null)
                await scheduler.ScheduleJob(AutomaticTrigger(plan, "startup").StartAt(DateTimeOffset.UtcNow.AddSeconds(2)).Build(), cancellationToken: token);
        }
        await RestorePendingAsync(token);
        await scheduler.Start(token);
    }
    private static TriggerBuilder<PluginJobDispatcher> AutomaticTrigger(TaskDefinition plan, string kind) => TriggerBuilder.Create<PluginJobDispatcher>()
        .WithIdentity(TriggerKey(Key(plan), kind)).ForJob(Key(plan))
        .UsingJobData("revision", plan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private async Task ApplyAsync(TaskDefinition plan, CancellationToken token)
    {
        Validate(plan.Cron, plan.TimeZoneId);
        foreach (var kind in new[] { "cron", "startup", "retry" }) await scheduler!.UnscheduleJob(TriggerKey(Key(plan), kind), token);
        if (plan.Enabled)
        {
            await scheduler!.ScheduleJob(AutomaticTrigger(plan, "cron")
                .WithCronSchedule(plan.Cron, x => x.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId))
                    .WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing)).Build(), cancellationToken: token);
            await ScheduleRetryAsync(plan, token);
        }
        repository.Reconciled(plan);
    }
    private async Task ScheduleRetryAsync(TaskDefinition plan, CancellationToken token)
    {
        await scheduler!.UnscheduleJob(TriggerKey(Key(plan), "retry"), token);
        if(repository.PendingRuns().Any(x=>x.PluginId==plan.PluginId && x.JobId==plan.JobId))return;
        if (plan.Enabled && plan.NotBefore is { } retry)
            await scheduler.ScheduleJob(AutomaticTrigger(plan, "retry").StartAt(retry > DateTimeOffset.UtcNow ? retry : DateTimeOffset.UtcNow).Build(), cancellationToken: token);
    }
    private async Task RestorePendingAsync(CancellationToken token)
    {
        foreach (var pending in repository.PendingRuns())
        {
            var plan = repository.Plans().SingleOrDefault(x => x.PluginId == pending.PluginId && x.JobId == pending.JobId);
            if (plan is null || !HasHandler(plan) || running.ContainsKey(Key(plan)) || queued.ContainsKey(Key(plan))) continue;
            var key = Key(plan); var nonce = Guid.NewGuid().ToString("N"); queued.Add(key, nonce);
            try
            {
                await scheduler!.UnscheduleJob(TriggerKey(key, "manual"), token);
                await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity(TriggerKey(key, "manual")).ForJob(key)
                    .UsingJobData("manual", nonce).UsingJobData("request", pending.RequestId.ToString("N"))
                    .StartAt(plan.NotBefore > DateTimeOffset.UtcNow ? plan.NotBefore.Value : DateTimeOffset.UtcNow).Build(), cancellationToken: token);
            }
            catch { queued.Remove(key); throw; }
        }
    }
    public async Task ReconcileAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (maintenance || fault is not null) return;
            foreach (var plan in repository.Plans().Where(x => x.NeedsReconcile && HasHandler(x))) await ApplyAsync(plan, token);
            await RestorePendingAsync(token);
            foreach (var plugin in pluginWriters.Keys) await WakeWaitingAsync(plugin, token);
        }
        catch (Exception error) when (StorageError(error)) { await TripFaultAsync("StorageFault"); }
        finally { gate.Release(); }
    }
    public async Task<BackgroundResponse> HandleAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token)
    {
        if (request.Command == "QueryPlugin") return await QueryPluginAsync(request, token);
        if(request.Command is "GetEmailSettings" or "SaveEmailSettings" or "TestEmailSettings")return await HandleEmailAsync(request,caller,token);
        await gate.WaitAsync(token);
        try
        {
            var response = await HandleCoreAsync(request, caller, token);
            if (fault is null && request.Command is not ("GetHealth" or "GetJobs" or "GetHistory" or "GetFilteredHistory" or "GetExecution" or "GetCommand" or "QueryPlugin"))
                repository.Audit(caller.Sid, request.RequestId, request.Command, response.Code);
            return response;
        }
        catch (Exception error) when (StorageError(error))
        {
            await TripFaultAsync("StorageFault");
            return new(WorkerProtocol.Version, request.RequestId, "StorageFault");
        }
        finally { gate.Release(); }
    }
    /// <summary>只在准入和退出时持全局锁；插件查询独立限额，维护时取消并排空后才允许释放会话。</summary>
    private async Task<BackgroundResponse> QueryPluginAsync(BackgroundRequest request, CancellationToken token)
    {
        BackgroundResponse Reply(string code, string? payload = null) => new(WorkerProtocol.Version, request.RequestId, code, Payload: payload);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.WaitAsync(token);
        try
        {
            if (fault is not null) return Reply(fault);
            if (catalog.RequiresProcessRestart) return Reply("PluginUnresponsive");
            if (maintenance) return Reply("Maintenance");
            if (request.PluginId is null || request.Payload is null || request.Payload.Length > 4096) return Reply("InvalidRequest");
            // 不允许一个慢插件在宿主内积累无界等待；其它插件和管理命令使用各自路径。
            if (!queryingPlugins.Add(request.PluginId)) return Reply("PluginBusy");
            activeQueries.Add(completed);
        }
        finally { gate.Release(); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, queryStopping.Token);
        try { return Reply("Ok", await catalog.QueryAsync(request.PluginId, request.Payload, cancellation.Token)); }
        catch (ArgumentException) { return Reply("UnknownQuery"); }
        catch (System.Text.Json.JsonException) { return Reply("InvalidRequest"); }
        catch (InvalidDataException) { return Reply("InvalidRequest"); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (PluginLifecycleTimeoutException) { throw; } // 不合作插件必须继续走宿主进程终止策略。
        catch (Exception error)
        {
            diagnostics?.Invoke("PluginQueryFailed", request.PluginId, error.GetType().Name);
            return Reply("PluginQueryFailed");
        }
        finally
        {
            await gate.WaitAsync(CancellationToken.None);
            try { queryingPlugins.Remove(request.PluginId); activeQueries.Remove(completed); completed.SetResult(); }
            finally { gate.Release(); }
        }
    }
    /// <summary>来源存储失效时保留宿主健康诊断；缺失的来源快照不能当作Ready或空额度。</summary>
    private async Task<IReadOnlyList<SourceBudgetHealth>?> SourceHealthAsync()
    {
        try{return sources?.Snapshot();}
        catch(Exception error) when(StorageError(error)||error is InvalidDataException or FormatException)
        {
            await TripFaultAsync("StorageFault");
            return null;
        }
    }
    private async Task<BackgroundResponse> HandleCoreAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token)
    {
        BackgroundResponse Reply(string code, CommandReceipt? receipt = null) => new(WorkerProtocol.Version, request.RequestId, code, Receipt: receipt);
        if (request.Command == "GetHealth")
        {
            var sourceHealth=await SourceHealthAsync();
            var state = fault ?? (catalog.RequiresProcessRestart ? "PluginUnresponsive" : maintenance ? "Maintenance" : catalog.Statuses.Any(x => x.State == "Failed") || repository.Plans().Any(x => x.NeedsReconcile || !HasHandler(x)) ? "Degraded" : "Running");
            return new(WorkerProtocol.Version, request.RequestId, "Ok", new(state, started, DateTimeOffset.UtcNow, Environment.ProcessId,
                SchedulingSnapshot(),sourceHealth), Plugins: catalog.Statuses.ToArray());
        }
        if (fault is not null) return Reply(fault);
        if (catalog.RequiresProcessRestart) return Reply("PluginUnresponsive");
        if (request.PageSize is < 1 or > 10) return Reply("InvalidRequest");
        if (request.Command == "GetJobs")
        {
            var plans = repository.Plans().Where(x=>!catalog.Jobs.Any(job=>job.PluginId==x.PluginId && job.Descriptor.JobId==x.JobId && job.Descriptor.ManualOnly))
                .Where(x => request.Cursor is null || string.CompareOrdinal(x.PluginId + ":" + x.JobId, request.Cursor) > 0).Take(request.PageSize + 1).ToArray();
            var page = plans.Take(request.PageSize).ToArray();
            return new(WorkerProtocol.Version, request.RequestId, "Ok", Jobs: page, NextCursor: plans.Length > page.Length ? page[^1].PluginId + ":" + page[^1].JobId : null);
        }
        // 新查询命令让旧服务明确返回不支持，避免忽略任务筛选后展示混合结果。
        if (request.Command is "GetHistory" or "GetFilteredHistory")
        {
            try { var page = repository.HistoryPage(request.Cursor, request.PageSize,request.PluginId,request.JobId); return new(WorkerProtocol.Version, request.RequestId, "Ok", History: page.Rows, NextCursor: page.NextCursor); }
            catch (ArgumentException) { return Reply("InvalidRequest"); }
        }
        if(request.Command=="GetExecution")
        {
            if(!Guid.TryParseExact(request.ExecutionId,"N",out var id))return Reply("InvalidRequest");
            var record=repository.FindExecution(id.ToString("N"));
            return new(WorkerProtocol.Version,request.RequestId,record is null?"UnknownExecution":"Ok",Execution:record);
        }
        if (request.Command == "GetCommand")
        {
            var receipt = request.RelatedRequestId is { } related ? repository.FindCommand(related, caller.Sid) : null;
            return Reply(receipt is null ? "UnknownRequest" : "Ok", receipt);
        }
        if (request.Command == "AuditDenied" || !caller.CanManage) return Reply("Forbidden");
        if (maintenance) return Reply("Maintenance");
        if (request.Command is not ("RunNow" or "Pause" or "Resume" or "ChangeSchedule" or "CancelRun")) return Reply("UnknownCommand");
        var plan = repository.Plans().SingleOrDefault(x => x.PluginId == request.PluginId && x.JobId == request.JobId);
        if (plan is null || !HasHandler(plan)) return Reply("JobUnavailable");
        if(request.Command is "Resume" or "ChangeSchedule" && catalog.Jobs.Single(x=>x.PluginId==plan.PluginId && x.Descriptor.JobId==plan.JobId).Descriptor.ManualOnly)
            return Reply("ManualOnly");
        if(request.Command=="RunNow" && request.Payload is null && catalog.Jobs.Single(x=>x.PluginId==plan.PluginId && x.Descriptor.JobId==plan.JobId).Descriptor.ManualOnly)
            return Reply("ParametersRequired");
        var accepted = false;
        try
        {
            var replayedRun=request.Command=="RunNow"&&repository.FindCommand(request.RequestId,caller.Sid) is not null;
            if (request.Command == "RunNow" && !replayedRun
                && !running.ContainsKey(Key(plan)) && repository.PendingRuns().Count(x => x.PluginId == plan.PluginId) >= settings.MaxPendingPerPlugin)
                return Reply("PluginBusy"); // 超配额的新命令不落盘，不制造无界待执行积压。
            var receipt = repository.AcceptCommand(request, caller.Sid); accepted = true;
            if (receipt.State != "Pending") return Reply(receipt.Code, receipt);
            // AcceptCommand先校验相同ID的调用者和原始参数；运行中的同一命令重放只返回原受理状态。
            if(replayedRun)return Reply("Accepted",receipt);
            var code = "Ok";
            switch (request.Command)
            {
                case "RunNow":
                    if (running.ContainsKey(Key(plan))) { code = "AlreadyRunning"; repository.CompleteCommand(request.RequestId, code); break; }
                    // 返回已接受之前已经提交加密待执行项；Quartz 应用失败由恢复循环继续处理。
                    try { await RestorePendingAsync(token); } catch (SchedulerException) { }
                    return Reply("Accepted", repository.FindCommand(request.RequestId, caller.Sid));
                case "CancelRun":
                    await CancelAsync(Key(plan), token); code = "Accepted"; break;
                default:
                    if (request.ExpectedRevision is null) { code = "RevisionRequired"; break; }
                    var next = request.Command switch
                    {
                        "Pause" => plan with { Enabled = false }, "Resume" => plan with { Enabled = true },
                        _ => plan with { Cron = request.Cron ?? "", TimeZoneId = request.TimeZoneId ?? "" }
                    };
                    Validate(next.Cron, next.TimeZoneId);
                    repository.Save(next, request.ExpectedRevision.Value, request.RequestId);
                    next = next with { Revision = plan.Revision + 1, NeedsReconcile = true };
                    if (!next.Enabled) await CancelAsync(Key(plan), token);
                    try { await ApplyAsync(next, token); } catch (SchedulerException) { code = "AcceptedNeedsReconcile"; }
                    break;
            }
            // Save 已将配置与命令完成状态同事务提交；重复请求返回原提交结果。
            if (repository.FindCommand(request.RequestId, caller.Sid)?.State != "Completed") repository.CompleteCommand(request.RequestId, code);
            return Reply(code, repository.FindCommand(request.RequestId, caller.Sid));
        }
        catch (Exception error) when (error is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or InvalidOperationException)
        {
            if (accepted) repository.CompleteCommand(request.RequestId, "InvalidOrStaleRequest");
            return Reply("InvalidOrStaleRequest");
        }
    }
    private async Task CancelAsync(JobKey key, CancellationToken token)
    {
        repository.CancelPending(key.Group, key.Name); queued.Remove(key);
        RemoveWaiting(key);
        await scheduler!.UnscheduleJob(TriggerKey(key, "manual"), token);
        await scheduler.UnscheduleJob(TriggerKey(key, "ready"), token);
        if (running.TryGetValue(key, out var source)) source.Cancel();
        await WakeWaitingAsync(key.Group, token);
    }
    private async Task ExecuteSerializedAsync(DispatchInvocation invocation, CancellationToken cancellationToken)
    {
        var key = invocation.Key;
        TaskDefinition? plan = null; CancellationTokenSource? source = null; string? execution = null; Guid? request = null;string? payload=null;
        await gate.WaitAsync(stopping.Token);
        try
        {
            if (maintenance || fault is not null || catalog.RequiresProcessRestart || running.ContainsKey(key)) return;
            plan = repository.Plans().SingleOrDefault(x => x.PluginId == key.Group && x.JobId == key.Name);
            if (plan is null || !HasHandler(plan)) return;
            var manual = invocation.Manual;
            if (manual is not null)
            {
                if (!queued.TryGetValue(key, out var nonce) || nonce != manual) return;
                queued.Remove(key); request = Guid.ParseExact(invocation.Request!, "N");
            }
            else if (!plan.Enabled || invocation.Revision != plan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || repository.PendingRuns().Any(x=>x.PluginId==plan.PluginId&&x.JobId==plan.JobId)) return;
            if (plan.NotBefore > DateTimeOffset.UtcNow) return;
            source = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken);
            source.CancelAfter(executionTimeout ?? TimeSpan.FromMinutes(15));
            if(request is { } requestId)payload=repository.ReadCommand(requestId).Payload;
            execution = repository.Begin(plan, invocation.ScheduledUtc, request);
            running.Add(key, source);
        }
        catch (Exception error) when (StorageError(error)) { source?.Dispose(); await TripFaultAsync("StorageFault"); return; }
        finally { gate.Release(); }
        if (execution is null || source is null || plan is null) return;
        BackgroundJobResult result;
        var handler = catalog.Jobs.Single(x => x.PluginId == plan.PluginId && x.Descriptor.JobId == plan.JobId);
        // 参数来自已提交的持久命令，强杀恢复后继续使用同一业务操作。
        var work = Task.Run(() => handler.Session is IProgressBackgroundSession progress
            ?progress.ExecuteWithProgressAsync(plan.JobId,payload,detail=>SaveProgressAsync(execution,detail,source.Token),source.Token,request)
            :handler.Session is IParameterizedBackgroundSession parameterized
            ?parameterized.ExecuteAsync(plan.JobId,payload,source.Token,request):handler.Session.ExecuteAsync(plan.JobId,source.Token), CancellationToken.None);
        try { result = await work.WaitAsync(source.Token); }
        catch (OperationCanceledException)
        {
            try { await work.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            {
                await gate.WaitAsync(CancellationToken.None);
                try { await TripFaultAsync("UnresponsivePlugin"); onUnresponsive?.Invoke(); }
                finally { gate.Release(); }
                // 不释放仍被插件使用的连接；服务停止超时后由宿主终止进程。
                try { await work; } catch (Exception) { }
            }
            catch (Exception) { }
            result = new("Cancelled");
        }
        // 仅记录异常类型，不能把插件异常消息中的凭据、地址参数或本机路径写入公开日志。
        catch (Exception error) { result = new("Failed","error="+error.GetType().Name[..Math.Min(error.GetType().Name.Length,128)]); }
        if (result.Status is not ("Succeeded" or "NotPublished" or "AccessBlocked" or "SourceSchemaChanged" or "RetryDeferred" or "Rejected" or "Failed" or "Cancelled") || result.Checkpoint?.Length > 1024) result = new("Failed");
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (fault == "StorageFault") return;
            repository.End(execution, plan, result, request);
            diagnostics?.Invoke("ExecutionCompleted", plan.PluginId + ":" + plan.JobId, result.Status);
            var updated = repository.Plans().Single(x => Key(x).Equals(key));
            await ScheduleRetryAsync(updated, CancellationToken.None);
        }
        catch (Exception error) when (StorageError(error)) { await TripFaultAsync("StorageFault"); }
        finally { running.Remove(key); source.Dispose(); gate.Release(); }
        if(request is not null && result.Status=="RetryDeferred")await ReconcileAsync(CancellationToken.None);
    }
    /// <summary>阶段记录与控制查询共享短数据库门槛，落盘失败立即停止采集，避免产生虚假进度。</summary>
    private async Task SaveProgressAsync(string execution,BackgroundProgress progress,CancellationToken token)
    {
        await gate.WaitAsync(token);
        try{repository.AppendProgress(execution,progress);}
        catch(Exception error) when(StorageError(error)){await TripFaultAsync("StorageFault");throw;}
        finally{gate.Release();}
    }
    public async Task EnterMaintenanceAsync(CancellationToken token)
    {
        Task[] queries;
        await gate.WaitAsync(token);
        try
        {
            maintenance = true;
            queries = activeQueries.Select(x => x.Task).ToArray();
            if (scheduler is not null && scheduler.Status != SchedulerStatus.Shutdown) await scheduler.Standby(token);
            queued.Clear();
            waiting.Clear(); foreach (var order in waitingOrder.Values) order.Clear(); wakeTickets.Clear();
            foreach (var source in running.Values) source.Cancel();
        }
        finally { gate.Release(); }
        queryStopping.Cancel(); // 插件取消回调不能在全局锁内执行。
        await Task.WhenAll(queries).WaitAsync(token);
        if (scheduler is not null && scheduler.Status != SchedulerStatus.Shutdown) await scheduler.Shutdown(waitForJobsToComplete: true, token);
    }
    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await EnterMaintenanceAsync(deadline.Token);
        stopping.Cancel(); stopping.Dispose(); queryStopping.Dispose(); foreach (var writer in pluginWriters.Values) writer.Dispose(); gate.Dispose();
    }
}

/// <summary>Quartz 按稳定 JobKey 防止重入，插件级写入门槛另约束共享 Session。</summary>
[DisallowConcurrentExecution]
public sealed class PluginJobDispatcher : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await ((BackgroundRuntime)context.Scheduler.Context["crs.worker.runtime"]!).DispatchAsync(context, cancellationToken);
}
