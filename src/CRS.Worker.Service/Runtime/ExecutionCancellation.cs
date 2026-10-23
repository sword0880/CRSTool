namespace CRS.Worker.Service;

/// <summary>先通知宿主取消已请求，再在锁外运行插件回调；回调不能阻断宿主的超时观察。</summary>
internal sealed class ExecutionCancellation : IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource source = new();
    private readonly TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration stopping, scheduler;
    private readonly Timer deadline;
    private readonly Action callbackFailed;
    private Task callbacks = Task.CompletedTask;
    private bool cancelled, closed;

    public ExecutionCancellation(CancellationToken stoppingToken, CancellationToken schedulerToken, TimeSpan timeout, Action callbackFailed)
    {
        this.callbackFailed = callbackFailed;
        stopping = stoppingToken.Register(Cancel);
        scheduler = schedulerToken.Register(Cancel);
        deadline = new Timer(_ => Cancel(), null, timeout, Timeout.InfiniteTimeSpan);
    }
    public CancellationToken Token => source.Token;
    public bool IsCancellationRequested => requested.Task.IsCompleted;
    public Task Requested => requested.Task;
    public Task Callbacks { get { lock (sync) return callbacks; } }
    public void Cancel()
    {
        lock (sync)
        {
            if (closed || cancelled) return;
            cancelled = true;
            // CancelAsync立即设置令牌状态，插件回调由线程池执行；不在runtime gate内等待。
            callbacks = ObserveAsync(source.CancelAsync());
            requested.TrySetResult();
        }
    }
    private async Task ObserveAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception) { try { callbackFailed(); } catch (Exception) { } }
    }
    public Task Complete()
    {
        // 先封闭新的取消入口，再截取回调；避免计时器在排空与Dispose之间启动迟到回调。
        lock (sync) { closed = true; }
        deadline.Dispose(); stopping.Dispose(); scheduler.Dispose();
        lock (sync) return callbacks;
    }
    public void Dispose() { Complete(); source.Dispose(); }
}
