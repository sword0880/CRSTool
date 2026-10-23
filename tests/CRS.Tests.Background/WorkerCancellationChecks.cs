using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

internal static class WorkerCancellationChecks
{
    private static readonly BackgroundCaller Admin = new("synthetic-cancellation-caller", true);
    private static BackgroundRequest Request(string command) => new(WorkerProtocol.Version, Guid.NewGuid(), command, "crs.cancel", "one", ExpectedRevision: 1);
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS 取消边界：" + label); }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
    private static BackgroundPluginCatalog Catalog(Session session)
    {
        var catalog = new BackgroundPluginCatalog();
        ((List<HostedBackgroundJob>)catalog.Jobs).Add(new("crs.cancel", session.DescribeJobs()[0], session));
        return catalog;
    }
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            await ClosedCancellation();
            await ResultRace(root, key, "CancelRun");
            await ResultRace(root, key, "Pause");
            await Callback(root, key, "CancelRun");
            await Callback(root, key, "Maintenance");
            await Callback(root, key, "StorageFault");
            await CallbackFailure(root, key, false);
            await CallbackFailure(root, key, true);
            await SuccessfulCleanup(root, key);
            await UnresponsiveCleanup(root, key);
            await FailedCleanup(root, key);
            await HealthFailure(root, key, "Plans");
            await HealthFailure(root, key, "PendingRuns");
            DurableCancellation(root, key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static async Task ClosedCancellation()
    {
        using var parent=new CancellationTokenSource();
        using var cancellation=new ExecutionCancellation(parent.Token,default,Timeout.InfiniteTimeSpan,()=>{});
        var callbacks=0;
        using var registration=cancellation.Token.Register(()=>Interlocked.Increment(ref callbacks));
        await cancellation.Complete();
        parent.Cancel();cancellation.Cancel();
        Check(callbacks==0&&!cancellation.IsCancellationRequested,"执行收尾封闭取消入口，父令牌和迟到期限不能再访问已完成会话");
    }
    private static async Task ResultRace(string root, byte[] key, string command)
    {
        var session = new Session();
        await using var catalog = Catalog(session);
        using var repository = new EncryptedTaskRepository(Path.Combine(root, command + ".db"), key);
        await using var runtime = new BackgroundRuntime(repository, catalog);
        await runtime.StartAsync(default);
        var run = Request("RunNow");
        await runtime.HandleAsync(run, Admin, default);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 确定性排列“取消先提交、旧执行结果后提交”，不依赖随机压力命中竞态。
        var gate = (SemaphoreSlim)typeof(BackgroundRuntime).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        await gate.WaitAsync();
        Task<BackgroundResponse> cancel;
        try
        {
            cancel = runtime.HandleAsync(Request(command), Admin, default);
            session.Result.SetResult(new("RetryDeferred", RetryAfter: TimeSpan.FromSeconds(30)));
            await Task.Delay(150);
        }
        finally { gate.Release(); }
        var reply = await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => repository.FindCommand(run.RequestId, Admin.Sid)?.State == "Completed");
        Check(reply.Code == (command == "Pause" ? "Ok" : "Accepted") && repository.FindCommand(run.RequestId, Admin.Sid)?.Code == "Cancelled"
            && repository.PendingRuns().Count == 0 && repository.Plans().Single().NotBefore is null,
            command + "先于延期结果提交时原请求不会复活，也不遗留自动重试期限");
    }
    private static async Task Callback(string root, byte[] key, string command)
    {
        var session = new Session { Reenter = true };
        await using var catalog = Catalog(session);
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "callback-" + command + ".db"), key);
        var failing = new FailingRepository(repository);
        await using var runtime = new BackgroundRuntime(failing, catalog);
        session.ReadHealth = () => runtime.HandleAsync(Request("GetHealth"), Admin, default);
        await runtime.StartAsync(default);
        var run = Request("RunNow");
        await runtime.HandleAsync(run, Admin, default);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (command == "Maintenance") await runtime.EnterMaintenanceAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        else if (command == "StorageFault")
        {
            failing.Failure = "Plans";
            await runtime.HandleAsync(Request("GetHealth"), Admin, default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        else await runtime.HandleAsync(Request(command), Admin, default).WaitAsync(TimeSpan.FromSeconds(5));
        var health = await session.CallbackHealth.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var expected = command == "Maintenance" ? "Maintenance" : command == "StorageFault" ? "StorageFault" : "Running";
        Check(health.Health?.State == expected, command + "取消回调可重入健康查询，不占住全局门闩");
    }
    private static async Task CallbackFailure(string root, byte[] key, bool hang)
    {
        var session = new Session { ThrowCallback = !hang, HangCallback = hang };
        var events = new ConcurrentQueue<string>();
        var unresponsive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var catalog = Catalog(session);
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "callback-failure-" + hang + ".db"), key);
        await using var runtime = new BackgroundRuntime(repository, catalog, (_, _, code) => events.Enqueue(code), () => unresponsive.TrySetResult());
        await runtime.StartAsync(default);
        var run = Request("RunNow");
        await runtime.HandleAsync(run, Admin, default);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Check((await runtime.HandleAsync(Request("CancelRun"), Admin, default).WaitAsync(TimeSpan.FromSeconds(5))).Code == "Accepted",
                (hang ? "阻塞" : "异常") + "取消回调不破坏管理响应");
            if (hang)
            {
                await unresponsive.Task.WaitAsync(TimeSpan.FromSeconds(6));
                var health = await runtime.HandleAsync(Request("GetHealth"), Admin, default).WaitAsync(TimeSpan.FromSeconds(2));
                Check(health.Health?.State == "UnresponsivePlugin", "回调阻塞仍在宽限期后触发安全停止，健康查询保持可用");
            }
            else
            {
                await Until(() => events.Contains("CallbackFailed"));
                Check((await runtime.HandleAsync(Request("GetHealth"), Admin, default)).Health?.State == "Running", "回调异常被观察和分类，不扩散为宿主退出");
            }
        }
        finally { session.ReleaseCallback.Set(); }
        await Until(() => repository.FindCommand(run.RequestId, Admin.Sid)?.State == "Completed");
    }
    private static async Task SuccessfulCleanup(string root, byte[] key)
    {
        var session = new Session { CommitPath = Path.Combine(root, "committed.txt") };
        await using var catalog = Catalog(session);
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "late-success.db"), key);
        await using var runtime = new BackgroundRuntime(repository, catalog);
        await runtime.StartAsync(default);
        var run = Request("RunNow");
        await runtime.HandleAsync(run, Admin, default);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.HandleAsync(Request("CancelRun"), Admin, default);
        await Until(() => repository.FindCommand(run.RequestId, Admin.Sid)?.State == "Completed");
        Check(File.ReadAllText(session.CommitPath) == "committed" && repository.History().Single().Result == "Succeeded"
            && repository.FindCommand(run.RequestId, Admin.Sid)?.Code == "Succeeded", "取消收尾期实际提交成功，历史和命令均保留Succeeded");
    }
    private static async Task UnresponsiveCleanup(string root, byte[] key)
    {
        var session = new Session { IgnoreCancellation = true };
        var unresponsive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var catalog = Catalog(session);
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "unresponsive.db"), key);
        await using var runtime = new BackgroundRuntime(repository, catalog, onUnresponsive: () => unresponsive.TrySetResult(), executionTimeout: TimeSpan.FromMilliseconds(100));
        await runtime.StartAsync(default);
        var run = Request("RunNow");
        await runtime.HandleAsync(run, Admin, default);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await unresponsive.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Check((await runtime.HandleAsync(Request("GetHealth"), Admin, default)).Health?.State == "UnresponsivePlugin"
                && repository.History().Single().Result == "Running", "执行超时且插件不合作时请求停止，不提前释放或伪造完成");
        }
        finally { session.Result.TrySetCanceled(); }
        await Until(() => repository.FindCommand(run.RequestId, Admin.Sid)?.State == "Completed");
    }
    private static async Task FailedCleanup(string root, byte[] key)
    {
        var session = new Session { ThrowOnCancel = true };
        var unresponsive = false;
        await using var catalog = Catalog(session);
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "cleanup-failed.db"), key);
        await using var runtime = new BackgroundRuntime(repository, catalog, onUnresponsive: () => unresponsive = true);
        await runtime.StartAsync(default);
        var run = Request("RunNow");
        await runtime.HandleAsync(run, Admin, default);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.HandleAsync(Request("CancelRun"), Admin, default);
        await Until(() => repository.FindCommand(run.RequestId, Admin.Sid)?.State == "Completed");
        Check(!unresponsive && repository.History().Single() is { Result: "Failed", Checkpoint: "error=TimeoutException" },
            "收尾已完成的TimeoutException记录实际失败，不误判为插件失控");
    }
    private static async Task HealthFailure(string root, byte[] key, string operation)
    {
        await using var catalog = new BackgroundPluginCatalog();
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "health-" + operation + ".db"), key);
        var failing = new FailingRepository(repository);
        await using var runtime = new BackgroundRuntime(failing, catalog);
        await runtime.StartAsync(default);
        failing.Failure = operation;
        for (var i = 0; i < 2; i++)
        {
            var health = await runtime.HandleAsync(Request("GetHealth"), Admin, default);
            Check(health.Code == "Ok" && health.Health is { State: "StorageFault", Scheduling: null }, operation + "持续不可读时健康仍有故障详情，未知调度不填零");
        }
        failing.Failure = null;
        await runtime.ReconcileAsync(default);
        Check((await runtime.HandleAsync(Request("GetHealth"), Admin, default)).Health?.State == "StorageFault", "存储短暂恢复不自动清除故障继续写入");
    }
    private static void DurableCancellation(string root, byte[] key)
    {
        var path = Path.Combine(root, "restart.db");
        var request = Request("RunNow");
        using (var repository = new EncryptedTaskRepository(path, key))
        {
            repository.RegisterDefault("crs.cancel", new("one", "fixture", "0 0 0 1 1 ?", "China Standard Time", false));
            repository.AcceptCommand(request, Admin.Sid);
            repository.Begin(repository.Plans().Single(), DateTimeOffset.UtcNow, request.RequestId);
            repository.CancelPending("crs.cancel", "one");
            Check(repository.FindCommand(request.RequestId, Admin.Sid) is { State: "Running", Code: "CancellationRequested" }, "运行中取消先持久化意图，不冒充业务已回滚");
        }
        using (var repository = new EncryptedTaskRepository(path, key))
        {
            Check(repository.PendingRuns().Count == 0 && repository.FindCommand(request.RequestId, Admin.Sid) is { State: "Completed", Code: "Interrupted" }
                && repository.History().Single().Result == "Interrupted", "取消收尾期间进程中断后不自动重放，保留结果未确认的中断事实");
            foreach (var status in new[] { "RetryDeferred", "Succeeded" })
            {
                var next = Request("RunNow");
                repository.AcceptCommand(next, Admin.Sid);
                var plan = repository.Plans().Single();
                var execution = repository.Begin(plan, DateTimeOffset.UtcNow, next.RequestId);
                repository.CancelPending("crs.cancel", "one");
                repository.End(execution, plan, new(status), next.RequestId);
                var expected = status == "RetryDeferred" ? "Cancelled" : "Succeeded";
                Check(repository.FindExecution(execution)?.Result == expected && repository.FindCommand(next.RequestId, Admin.Sid)?.Code == expected
                    && repository.PendingRuns().Count == 0, "仓储事务对取消后" + status + "保留正确结果且不复活命令");
            }
        }
    }
    private sealed class Session : IBackgroundPluginSession
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BackgroundJobResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BackgroundResponse> CallbackHealth { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseCallback { get; } = new();
        public Func<Task<BackgroundResponse>>? ReadHealth { get; set; }
        public bool Reenter { get; init; }
        public bool ThrowCallback { get; init; }
        public bool HangCallback { get; init; }
        public bool IgnoreCancellation { get; init; }
        public bool ThrowOnCancel { get; init; }
        public string? CommitPath { get; init; }
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs() => [new("one", "取消夹具", "0 0 0 1 1 ?", "China Standard Time", false)];
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public async Task<BackgroundJobResult> ExecuteAsync(string job, CancellationToken token)
        {
            var pending = Result.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : token);
            using var registration = token.Register(() =>
            {
                if (Reenter) CallbackHealth.TrySetResult(ReadHealth!().GetAwaiter().GetResult());
                if (ThrowCallback) throw new InvalidOperationException("synthetic callback");
                if (HangCallback) ReleaseCallback.Wait();
            });
            Entered.TrySetResult();
            try { return await pending; }
            catch (OperationCanceledException) when (CommitPath is not null || ThrowOnCancel)
            {
                if (ThrowOnCancel) throw new TimeoutException("synthetic completed failure");
                await Task.Delay(80);
                File.WriteAllText(CommitPath!, "committed");
                return BackgroundJobResult.Success("synthetic committed write");
            }
        }
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FailingRepository(ITaskRepository inner) : ITaskRepository
    {
        public string? Failure { get; set; }
        public List<TaskDefinition> Plans() => Failure == "Plans" ? throw new IOException("synthetic storage") : inner.Plans();
        public List<(Guid RequestId, string PluginId, string JobId)> PendingRuns() => Failure == "PendingRuns" ? throw new IOException("synthetic storage") : inner.PendingRuns();
        public void RegisterDefault(string p, BackgroundJobDescriptor d) => inner.RegisterDefault(p, d);
        public void Save(TaskDefinition p, long r, Guid? q = null) => inner.Save(p, r, q);
        public void Reconciled(TaskDefinition p) => inner.Reconciled(p);
        public string Begin(TaskDefinition p, DateTimeOffset s, Guid? q = null) => inner.Begin(p, s, q);
        public void End(string id, TaskDefinition p, BackgroundJobResult r, Guid? q = null) => inner.End(id, p, r, q);
        public List<ExecutionRecord> History() => inner.History();
        public ExecutionRecord? FindExecution(string id) => inner.FindExecution(id);
        public void AppendProgress(string id, BackgroundProgress p) => inner.AppendProgress(id, p);
        public (List<ExecutionRecord> Rows, string? NextCursor) HistoryPage(string? c, int n, string? p = null, string? j = null, string? f = null, DateOnly? d = null) => inner.HistoryPage(c, n, p, j, f, d);
        public void Audit(string s, Guid q, string c, string r) => inner.Audit(s, q, c, r);
        public CommandReceipt? FindCommand(Guid q, string s) => inner.FindCommand(q, s);
        public BackgroundRequest ReadCommand(Guid q) => inner.ReadCommand(q);
        public CommandReceipt? ReadCommandState(Guid q) => inner.ReadCommandState(q);
        public CommandReceipt AcceptCommand(BackgroundRequest q, string s) => inner.AcceptCommand(q, s);
        public void CompleteCommand(Guid q, string c) => inner.CompleteCommand(q, c);
        public void CancelPending(string p, string j) => inner.CancelPending(p, j);
    }
}
