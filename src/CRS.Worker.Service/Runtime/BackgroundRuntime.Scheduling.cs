using CRS.Worker.Ipc;
using Quartz;

namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    private sealed record DispatchInvocation(JobKey Key, DateTimeOffset ScheduledUtc, string? Manual, string? Request, string? Revision);
    private sealed record WaitingInvocation(string Id, DispatchInvocation Invocation);
    private readonly Dictionary<JobKey, WaitingInvocation> waiting = [];
    private readonly Dictionary<string, LinkedList<JobKey>> waitingOrder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> pluginLimits = new(StringComparer.Ordinal);
    private readonly Dictionary<JobKey, string> wakeTickets = [];
    private readonly HashSet<string> saturatedPlugins = new(StringComparer.Ordinal);
    private long coalescedTriggers, skippedTriggers;

    private BackgroundScheduling SchedulingSnapshot() => new(settings.EffectiveMaxConcurrency, running.Count, waiting.Count,
        settings.MaxPendingPerPlugin, coalescedTriggers, skippedTriggers, repository.PendingRuns().Count,
        pluginLimits.Select(x => new BackgroundPluginScheduling(x.Key, x.Value,
            running.Keys.Count(key => key.Group == x.Key), waitingOrder[x.Key].Count)).ToArray());

    // 以下队列状态全部由runtime gate保护；队列只保存有界标识/时间，不复制原件或任务参数。
    private void RemoveWaiting(JobKey key)
    {
        waiting.Remove(key); wakeTickets.Remove(key);
        if (waitingOrder.TryGetValue(key.Group, out var order))
        {
            order.Remove(key);
            if (order.Count < settings.MaxPendingPerPlugin) saturatedPlugins.Remove(key.Group);
        }
    }
    private bool IsCurrent(DispatchInvocation invocation)
    {
        var plan = repository.Plans().SingleOrDefault(x => Key(x).Equals(invocation.Key));
        if (catalog.RequiresProcessRestart || plan is null || !HasHandler(plan)) return false;
        return invocation.Manual is { } nonce
            ? queued.TryGetValue(invocation.Key, out var expected) && expected == nonce
            : plan.Enabled && invocation.Revision == plan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private bool Enqueue(DispatchInvocation invocation)
    {
        var key = invocation.Key; var order = waitingOrder[key.Group];
        if (waiting.TryGetValue(key, out var existing))
        {
            // 同任务只保留一个等待项；显式手动参数优先，自动重复触发不生成积压。
            if (invocation.Manual is not null && existing.Invocation.Manual is null)
                waiting[key] = existing with { Invocation = invocation with { ScheduledUtc = existing.Invocation.ScheduledUtc } };
            coalescedTriggers++;
            return true;
        }
        if (order.Count >= settings.MaxPendingPerPlugin)
        {
            var automatic = invocation.Manual is not null ? order.FirstOrDefault(x => waiting[x].Invocation.Manual is null) : null;
            if (automatic is not null) { RemoveWaiting(automatic); skippedTriggers++; }
            else
            {
                skippedTriggers++;
                if (invocation.Manual is not null) queued.Remove(key); // 已接受的旧版超额命令仍在仓储，协调循环会重建。
                if (saturatedPlugins.Add(key.Group)) diagnostics?.Invoke("ExecutionDeferred", key.Group, "QueueCapacity");
                return false;
            }
        }
        waiting.Add(key, new(Guid.NewGuid().ToString("N"), invocation)); order.AddLast(key);
        return true;
    }
    private async Task WakeWaitingAsync(string plugin, CancellationToken token)
    {
        if (maintenance || fault is not null || catalog.RequiresProcessRestart || pluginWriters[plugin].CurrentCount == 0) return;
        var head = waitingOrder[plugin].First?.Value;
        if (head is null) return;
        var ticket = waiting[head];
        if (wakeTickets.TryGetValue(head, out var scheduled) && scheduled == ticket.Id) return;
        var trigger = TriggerBuilder.Create().WithIdentity(TriggerKey(head, "ready")).ForJob(head)
            .UsingJobData("ready", ticket.Id).StartNow().Build();
        try
        {
            if (await scheduler!.RescheduleJob(trigger.Key, trigger, token) is null)
                await scheduler.ScheduleJob(trigger, cancellationToken: token);
            wakeTickets[head] = ticket.Id;
        }
        catch (SchedulerException)
        {
            wakeTickets.Remove(head); // 保留等待项，Reconcile会重试；不把未执行工作伪装完成。
            diagnostics?.Invoke("ExecutionDeferred", plugin, "SchedulerReconcile");
        }
    }
    internal async Task DispatchAsync(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var key = context.JobDetail.Key;
        DispatchInvocation? admitted = null;
        SemaphoreSlim? permit = null;
        try
        {
            await gate.WaitAsync(stopping.Token);
            try
            {
                if (maintenance || fault is not null || catalog.RequiresProcessRestart || !pluginWriters.TryGetValue(key.Group, out var writer)) return;
                if (context.MergedJobDataMap.GetString("ready") is { } ready)
                {
                    if (wakeTickets.TryGetValue(key, out var expected) && expected == ready) wakeTickets.Remove(key);
                    if (!waiting.TryGetValue(key, out var pending) || pending.Id != ready)
                    { await WakeWaitingAsync(key.Group, cancellationToken); return; }
                }
                else
                {
                    var incoming = new DispatchInvocation(key, context.ScheduledFireTimeUtc ?? CRS.Timing.BeijingClock.Now,
                        context.MergedJobDataMap.GetString("manual"), context.MergedJobDataMap.GetString("request"), context.MergedJobDataMap.GetString("revision"));
                    if (!IsCurrent(incoming) || !Enqueue(incoming)) return;
                }
                var ticket = waiting[key];
                if (!IsCurrent(ticket.Invocation))
                { RemoveWaiting(key); await WakeWaitingAsync(key.Group, cancellationToken); return; }
                if (waitingOrder[key.Group].First?.Value.Equals(key) == true && writer.Wait(0))
                {
                    permit = writer; admitted = ticket.Invocation; RemoveWaiting(key);
                    await WakeWaitingAsync(key.Group, cancellationToken);
                }
                else await WakeWaitingAsync(key.Group, cancellationToken);
            }
            catch (Exception error) when (StorageError(error)) { await TripFaultAsync("StorageFault"); }
            finally { gate.Release(); }
            if (admitted is not null) await ExecuteSerializedAsync(admitted, cancellationToken);
        }
        finally
        {
            if (permit is not null)
            {
                await gate.WaitAsync(CancellationToken.None);
                try
                {
                    permit.Release();
                    if (!maintenance && fault is null && !catalog.RequiresProcessRestart)
                    {
                        await WakeWaitingAsync(key.Group, CancellationToken.None);
                        await RestorePendingAsync(CancellationToken.None);
                    }
                }
                catch (Exception error) when (StorageError(error)) { await TripFaultAsync("StorageFault"); }
                catch (SchedulerException) { /* 持久命令和等待项由Reconcile恢复，禁止释放资源后重复执行。 */ }
                finally { gate.Release(); }
            }
        }
    }
}
