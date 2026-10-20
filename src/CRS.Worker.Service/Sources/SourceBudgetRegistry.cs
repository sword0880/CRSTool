using CRS.Worker.Abstractions;

namespace CRS.Worker.Service;

/// <summary>单个Worker唯一持有；同来源串行、三秒间隔、跨插件FIFO，最多32个等待且每插件最多8个。</summary>
public sealed class SourceBudgetRegistry : IDisposable
{
    private sealed class Waiter(string plugin)
    {
        public string Plugin { get; } = plugin;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Budget
    {
        public LinkedList<Waiter> Queue { get; } = [];
        public bool Active;
        public long Deferred, Rejected;
    }
    private readonly object sync = new();
    private readonly ISourceBudgetStore store;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, Budget> budgets = new(StringComparer.Ordinal);
    private bool disposed;
    public SourceBudgetRegistry(ISourceBudgetStore store, TimeProvider? clock = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? TimeProvider.System;
        try
        {
            foreach (var source in new[] { "SSE", "SZSE", "ISSUER-600153" })
            {
                _=store.Read(source); // 在任何插件/调度启动前验证持久状态，不能把损坏行当成空预算。
                budgets.Add(source,new());
            }
        }
        catch{store.Dispose();throw;}
    }
    private static string Source(string host) => host.ToLowerInvariant() switch
    {
        "www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "static.sse.com.cn" => "SSE",
        "www.szse.cn" or "disc.static.szse.cn" => "SZSE",
        "www.chinacnd.com" => "ISSUER-600153",
        _ => throw new ArgumentException("来源域名尚未获准使用共享预算。", nameof(host))
    };
    public async Task<IBackgroundSourceLease> AcquireAsync(string plugin, string host, SourceBudgetState legacy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = Source(host);
        LinkedListNode<Waiter> node;
        Budget budget;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            budget = budgets[source];
            var state = Merge(source,legacy);
            if (state.BlockedUtc > clock.GetUtcNow()) { budget.Rejected++; throw new SourceBudgetUnavailableException("SourceCooldown", state.BlockedUtc); }
            var waiting = budget.Queue.Count - (budget.Active ? 1 : 0);
            if (waiting >= 32 || budget.Queue.Count(x => x.Plugin == plugin) >= 8)
            { budget.Rejected++; throw new SourceBudgetUnavailableException("SourceQueueFull"); }
            node = budget.Queue.AddLast(new Waiter(plugin));
            if (node == budget.Queue.First) node.Value.Ready.TrySetResult();
            else budget.Deferred++;
        }
        try
        {
            await node.Value.Ready.Task.WaitAsync(token).ConfigureAwait(false);
            while (true)
            {
                TimeSpan wait;
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    token.ThrowIfCancellationRequested();
                    var state = store.Read(source);
                    if (state.BlockedUtc > clock.GetUtcNow()) { budget.Rejected++; throw new SourceBudgetUnavailableException("SourceCooldown", state.BlockedUtc); }
                    wait = state.NextUtc - clock.GetUtcNow();
                    if (wait <= TimeSpan.Zero)
                    {
                        // 必须先持久登记间隔才能让调用者访问网络。
                        store.Save(source, state with { NextUtc = clock.GetUtcNow().AddSeconds(3) });
                        budget.Active = true;
                        return new Lease(this, source, node);
                    }
                    budget.Deferred++;
                }
                await Task.Delay(wait, clock, token).ConfigureAwait(false);
            }
        }
        catch { Release(source, node); throw; }
    }
    public void Import(string host,SourceBudgetState legacy)
    {lock(sync){ObjectDisposedException.ThrowIf(disposed,this);Merge(Source(host),legacy);}}
    private SourceBudgetState Merge(string source,SourceBudgetState legacy)
    {
        var state=store.Read(source);
        // 合并旧插件状态只可延长等待；绝不以新库空状态覆盖已有拒绝。
        var merged=new SourceBudgetState(Max(state.NextUtc,legacy.NextUtc),Max(state.BlockedUtc,legacy.BlockedUtc),Math.Max(state.Failures,Math.Clamp(legacy.Failures,0,16)));
        if(merged!=state)store.Save(source,merged);
        return merged;
    }
    public void ReportFailure(string host, int status, DateTimeOffset? retryAfter = null)
    { lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); Failure(Source(host), status, retryAfter); } }
    private void Failure(string source, int status, DateTimeOffset? retryAfter)
    {
        var state = store.Read(source);
        var failures = Math.Min(state.Failures + 1, 16);
        var seconds = status is 403 or 429 ? 21600 : Math.Min(21600, 300 * Math.Pow(2, Math.Min(failures - 1, 7)));
        var until = clock.GetUtcNow().AddSeconds(seconds * (1 + Random.Shared.NextDouble() * .2));
        store.Save(source, state with { BlockedUtc = Max(Max(until, retryAfter ?? DateTimeOffset.MinValue), state.BlockedUtc), Failures = failures });
    }
    private void Complete(string source, int status, DateTimeOffset? retryAfter)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (status is 0 or 403 or 429 or >= 500) Failure(source, status, retryAfter);
            else if (status is >= 200 and < 300)
            {
                var state = store.Read(source);
                // 迟到成功不能清除其他校验反馈刚写入的冷却。
                if (state.BlockedUtc <= clock.GetUtcNow()) store.Save(source, state with { Failures = 0 });
            }
        }
    }
    private void Release(string source, LinkedListNode<Waiter> node)
    {
        lock (sync)
        {
            var budget = budgets[source];
            if (node.List is null) return;
            var first = node == budget.Queue.First;
            budget.Queue.Remove(node);
            if (first) { budget.Active = false; budget.Queue.First?.Value.Ready.TrySetResult(); }
        }
    }
    public IReadOnlyList<SourceBudgetHealth> Snapshot()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return budgets.Select(pair =>
            {
                var state = store.Read(pair.Key); var budget = pair.Value;
                var kind = state.BlockedUtc > clock.GetUtcNow() ? "Cooldown" : state.NextUtc > clock.GetUtcNow() ? "RateWait" : "Ready";
                return new SourceBudgetHealth(pair.Key, kind, budget.Active ? 1 : 0, budget.Queue.Count - (budget.Active ? 1 : 0),
                    state.NextUtc, state.BlockedUtc, state.Failures, budget.Deferred, budget.Rejected);
            }).ToArray();
        }
    }
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var budget in budgets.Values)
                foreach (var waiter in budget.Queue) waiter.Ready.TrySetException(new ObjectDisposedException(nameof(SourceBudgetRegistry)));
            store.Dispose();
        }
    }
    private sealed class Lease(SourceBudgetRegistry owner, string source, LinkedListNode<Waiter> node) : IBackgroundSourceLease
    {
        private int completed, released;
        public void Complete(int status, DateTimeOffset? retryAfter = null)
        {
            if (Volatile.Read(ref released) != 0) throw new ObjectDisposedException(nameof(Lease));
            if (Interlocked.CompareExchange(ref completed, 1, 0) == 0) owner.Complete(source, status, retryAfter);
        }
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(source, node); }
    }
}
