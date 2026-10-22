using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using Microsoft.Data.Sqlite;

/// <summary>验证整改针对的故障时序，全部使用隔离数据库、真实插件和测试自有进程。</summary>
internal static class WorkerRemediationChecks
{
    private static int passed;
    private static readonly string Sid = WindowsIdentity.GetCurrent().User!.Value;
    private static readonly BackgroundCaller Admin = new(Sid, true);
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS 整改：" + name); }
    private static BackgroundRequest Request(string command, string plugin = "crs.fixture", string job = "one", long? revision = null) => new(WorkerProtocol.Version, Guid.NewGuid(), command, plugin, job, ExpectedRevision: revision);
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!await condition()) { if (DateTimeOffset.UtcNow > end) throw new TimeoutException("整改验收等待超时。"); await Task.Delay(25); }
    }
    internal static (string Root, string Trust) Package(string root, params (string Id, string Entry)[] plugins)
    {
        var directory = Path.Combine(root, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var index = new List<TrustedPackage>();
        foreach (var (id, entry) in plugins)
        {
            var relative = id + "/1.0.0"; var path = Path.Combine(directory, relative); Directory.CreateDirectory(path);
            var dll = "CRS.Worker.FixturePlugin.dll"; File.Copy(Path.Combine(AppContext.BaseDirectory, dll), Path.Combine(path, dll));
            File.WriteAllText(Path.Combine(path, "plugin.json"), JsonSerializer.Serialize(new BackgroundManifest(id, "1.0.0", "2.0", new(dll, "CRS.Worker.FixturePlugin." + entry))));
            var hashes = Directory.EnumerateFiles(path).ToDictionary(file => Path.GetFileName(file)!, file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            index.Add(new(id, "1.0.0", relative, hashes));
        }
        var trust = Path.Combine(directory, "trusted.json"); File.WriteAllText(trust, JsonSerializer.Serialize(index)); return (directory, trust);
    }
    public static async Task RunAsync(string root, string servicePackage)
    {
        // 纯查询插件不需要为可发现性创建虚构 Job。
        var queryOnlyPackage = Package(root, ("crs.query-only", "QueryOnlyEntry"));
        await using(var queryOnly = new BackgroundPluginCatalog())
        {
            await queryOnly.LoadAsync(queryOnlyPackage.Root,queryOnlyPackage.Trust,default);
            Check(queryOnly.Jobs.Count==0 && queryOnly.Statuses.Single().State=="Started","无任务插件仍完成会话启动");
            Check(await queryOnly.QueryAsync("crs.query-only","ping",default)=="fixture-query-ok","纯查询插件的查询能力独立注册");
            queryOnly.Quarantine("crs.query-only","SyntheticQuarantine");
            try { await queryOnly.QueryAsync("crs.query-only","ping",default);throw new InvalidOperationException("隔离后仍接受查询"); }
            catch(ArgumentException) { Check(true,"隔离后删除查询注册"); }
        }
        var package = Package(root, ("crs.fixture", "GoodEntry"));
        await using var catalog = new BackgroundPluginCatalog(); await catalog.LoadAsync(package.Root, package.Trust, default);
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            await FaultsAsync(root, catalog, key);
            await PagingAndCapacityAsync(root, catalog, key);
            await LifecycleAsync(root);
            await PendingRecoveryAsync(root, package, servicePackage);
            await ConcurrencyAsync(root, catalog, key);
            await QueryIsolationAsync(root, key);
            Migration(root, key, catalog.Jobs[0].Descriptor);
            await RetryAsync(root, key, catalog);
            var badJob=Package(root,("crs.fixture","BadJobEntry"));
            await using(var failingCatalog=new BackgroundPluginCatalog())
            {
                await failingCatalog.LoadAsync(badJob.Root,badJob.Trust,default);
                using var failingRepository=new EncryptedTaskRepository(Path.Combine(root,"public-error.db"),key);
                await using var failingRuntime=new BackgroundRuntime(failingRepository,failingCatalog);await failingRuntime.StartAsync(default);
                await failingRuntime.HandleAsync(Request("RunNow"),Admin,default);
                await UntilAsync(()=>Task.FromResult(failingRepository.History().Any(x=>x.Result=="Failed")));
                Check(failingRepository.History().Single().Checkpoint=="error=InvalidOperationException","异常日志只持久化分类，不保存插件消息中的敏感标记");
            }
            await UnresponsiveAsync(root, key);
            var invalidSchedule = Package(root, ("crs.bad-plan", "BadScheduleEntry"), ("crs.good", "GoodEntry"));
            await using (var mixed = new BackgroundPluginCatalog())
            {
                await mixed.LoadAsync(invalidSchedule.Root, invalidSchedule.Trust, default);
                using var plans = new EncryptedTaskRepository(Path.Combine(root, "bad-schedule.db"), key);
                await using var runtime = new BackgroundRuntime(plans, mixed); await runtime.StartAsync(default);
                Check(plans.Plans().Count == 2 && (await runtime.HandleAsync(Request("GetHealth"), Admin, default)).Health?.State == "Degraded", "插件非法调度声明只隔离该插件，其它计划继续注册");
            }
            using var repository = new EncryptedTaskRepository(Path.Combine(root, "waiting.db"), key);
            repository.RegisterDefault("crs.fixture", catalog.Jobs[0].Descriptor); var plan = repository.Plans().Single();
            var execution = repository.Begin(plan, DateTimeOffset.UtcNow); repository.End(execution, plan, new("NotPublished", RetryAfter: TimeSpan.FromSeconds(40)));
            Check(repository.Plans().Single() is { Failures: 0, NotBefore: not null }, "未发布只记录等待期限，不累计故障次数");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        Console.WriteLine($"Worker 整改专项通过：{passed} 项。");
    }
    private static SqliteConnection RawDatabase(string path, byte[] key)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Password = Convert.ToHexString(key), Pooling = false }.ToString()); connection.Open(); return connection;
    }
    private static void Migration(string root, byte[] key, BackgroundJobDescriptor descriptor)
    {
        var path = Path.Combine(root, "schema1-fixture.db");
        using (var repository = new EncryptedTaskRepository(path, key))
        {
            repository.RegisterDefault("crs.fixture", descriptor);
            var plan = repository.Plans().Single(); var id = repository.Begin(plan, DateTimeOffset.UtcNow); repository.End(id, plan, BackgroundJobResult.Success("legacy-fact"));
        }
        // 明确重建上一版真实表结构，验证新表迁移与旧加密事实共存。
        using (var old = RawDatabase(path, key))
        {
            using var command = old.CreateCommand(); command.CommandText = "DROP TABLE command_request; CREATE UNIQUE INDEX audit_request ON audit(request); PRAGMA user_version=1"; command.ExecuteNonQuery();
        }
        using (var migrated = new EncryptedTaskRepository(path, key)) Check(migrated.History().Single().Checkpoint == "legacy-fact", "Schema 1 事务迁移保留旧计划和执行事实");
        var backup = Directory.EnumerateFiles(Path.Combine(root, "backup"), "schema1-fixture.db.schema1-*.db").Single();
        var encryptedHeader = !System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(backup), 0, 16).StartsWith("SQLite format");
        using var snapshot = RawDatabase(backup, key); using var query = snapshot.CreateCommand(); query.CommandText = "PRAGMA user_version";
        Check(Convert.ToInt32(query.ExecuteScalar()) == 1 && encryptedHeader, "迁移前自动保留可解密且仍为旧 Schema 的加密快照");
    }
    private static async Task RetryAsync(string root, byte[] key, BackgroundPluginCatalog catalog)
    {
        var path = Path.Combine(root, "retry.db"); using var repository = new EncryptedTaskRepository(path, key);
        repository.RegisterDefault("crs.fixture", catalog.Jobs[0].Descriptor with { EnabledByDefault = true });
        var plan = repository.Plans().Single(); var id = repository.Begin(plan, DateTimeOffset.UtcNow); repository.End(id, plan, new("NotPublished"));
        // 将已持久的等待时点放到近期，验证真实 Quartz 到期唤醒，生产最短退避仍为30秒。
        using (var database = RawDatabase(path, key))
        {
            using var command = database.CreateCommand(); command.CommandText = "UPDATE task SET not_before=$n"; command.Parameters.AddWithValue("$n", DateTimeOffset.UtcNow.AddMilliseconds(200).ToString("O")); command.ExecuteNonQuery();
        }
        await using var runtime = new BackgroundRuntime(repository, catalog); await runtime.StartAsync(default);
        await UntilAsync(() => Task.FromResult(repository.History().Any(x => x.Result == "Succeeded")));
        Check(repository.Plans().Single(x => x.JobId == "one").NotBefore is null, "重启按持久等待时点恢复一次性重试，成功后清除等待");
    }
    private static async Task UnresponsiveAsync(string root, byte[] key)
    {
        var package = Package(root, ("crs.fixture", "HangingJobEntry")); await using var catalog = new BackgroundPluginCatalog(); await catalog.LoadAsync(package.Root, package.Trust, default);
        var hold = Path.Combine(root, "job-hold.fixture"); File.WriteAllText(hold, "仅测试执行超时"); var previous = Environment.GetEnvironmentVariable("CRS_WORKER_TEST_HOLD");
        Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD", hold);
        try
        {
            using var repository = new EncryptedTaskRepository(Path.Combine(root, "unresponsive.db"), key); var notified = false;
            await using var runtime = new BackgroundRuntime(repository, catalog, onUnresponsive: () => notified = true, executionTimeout: TimeSpan.FromMilliseconds(100)); await runtime.StartAsync(default);
            await runtime.HandleAsync(Request("RunNow"), Admin, default);
            await UntilAsync(() => Task.FromResult(notified));
            Check((await runtime.HandleAsync(Request("GetHealth"), Admin, default)).Health?.State == "UnresponsivePlugin", "执行不响应取消时暴露宿主故障并请求安全停止");
            File.Delete(hold);
            await UntilAsync(() => Task.FromResult(repository.History().Any(x => x.Result == "Cancelled")));
        }
        finally { if (File.Exists(hold)) File.Delete(hold); Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD", previous); }
    }
    internal static async Task StorageFaultsOnlyAsync(string root)
    {
        Directory.CreateDirectory(root);
        var package=Package(root,("crs.fixture","GoodEntry"));
        await using var catalog=new BackgroundPluginCatalog();
        await catalog.LoadAsync(package.Root,package.Trust,default);
        var key=RandomNumberGenerator.GetBytes(32);
        try { await FaultsAsync(root,catalog,key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static async Task FaultsAsync(string root, BackgroundPluginCatalog catalog, byte[] key)
    {
        foreach (var providerFailure in new[] { false, true })
        foreach (var operation in new[] { "Begin", "End", "Audit", "Progress" })
        {
            using var repository = new EncryptedTaskRepository(Path.Combine(root, "fault-" + operation + "-" + providerFailure + ".db"), key);
            var failing = new FaultRepository(repository) { Failure = operation, ProviderFailure=providerFailure };
            await using var runtime = new BackgroundRuntime(failing, catalog); await runtime.StartAsync(default);
            var request = Request(operation == "Audit" ? "Pause" : "RunNow", revision: operation == "Audit" ? 1 : null);
            await runtime.HandleAsync(request, Admin, default);
            await UntilAsync(async () => (await runtime.HandleAsync(Request("GetHealth"), Admin, default)).Health?.State == "StorageFault");
            Check((await runtime.HandleAsync(Request("RunNow"), Admin, default)).Code == "StorageFault", operation + " 持久化失败关闭新任务且健康可读");
            var count = repository.History().Count; await runtime.ReconcileAsync(default); await Task.Delay(100);
            Check(repository.History().Count == count, operation + " 故障不会被重建循环静默放行");
            if (operation == "Audit") Check(repository.Plans().First(x => x.JobId == "one").Revision == 2, "配置已提交但响应不确定时保留原修订");
        }
    }
    private static async Task PagingAndCapacityAsync(string root, BackgroundPluginCatalog catalog, byte[] key)
    {
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "paging.db"), key);
        repository.RegisterDefault("crs.fixture", catalog.Jobs[0].Descriptor); var plan = repository.Plans().Single();
        for (var i = 0; i < 100; i++) { var id = repository.Begin(plan, DateTimeOffset.UtcNow); repository.End(id, plan, BackgroundJobResult.Success(new string('中', 1024))); }
        await using var runtime = new BackgroundRuntime(repository, catalog); await runtime.StartAsync(default);
        var pipe = "CRS.Worker.Review." + Guid.NewGuid().ToString("N"); using var stop = new CancellationTokenSource();
        var listening = new BackgroundPipeServer(runtime, pipe).RunAsync(stop.Token); var client = new BackgroundPipeClient(pipe);
        var slow = new List<NamedPipeClientStream>();
        try
        {
            string? cursor = null; var rows = new HashSet<string>(); var pages = 0;
            do
            {
                var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetHistory", Cursor: cursor));
                if (response.Code != "Ok" || JsonSerializer.SerializeToUtf8Bytes(response).Length > 131072) throw new InvalidOperationException("中文检查点分页超过消息预算。");
                pages++;
                foreach (var row in response.History!) if (!rows.Add(row.ExecutionId)) throw new InvalidOperationException("分页记录重复。");
                cursor = response.NextCursor;
            } while (cursor is not null);
            Check(rows.Count == 100 && pages == 10, "分页无遗漏读取全部长检查点历史");
            // 新只读命令必须把组合筛选送到仓储，旧服务会明确拒绝而不是忽略任务条件。
            var filtered=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory",PluginId:plan.PluginId,JobId:plan.JobId));
            Check(filtered.Code=="Ok" && filtered.History?.Count==10 && filtered.History.All(x=>x.PluginId==plan.PluginId && x.JobId==plan.JobId),"真实管道支持插件及任务组合筛选");
            var empty=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory",JobId:"no-such-job"));
            Check(empty.Code=="Ok" && empty.History?.Count==0,"真实管道任务筛选无匹配返回空页");
            // 明确按ID查询，不能仅在首屏十条历史中搜索日志。
            var oldest=repository.History().Last();
            var detail=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:oldest.ExecutionId));
            Check(detail.Code=="Ok" && detail.Execution==oldest,"真实管道按执行标识读取非首屏长检查点日志");
            Check((await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:oldest.ExecutionId),new("reader",false),default)).Execution==oldest,"普通查询身份可以读取公开执行日志");
            Check((await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:Guid.NewGuid().ToString("N")))).Code=="UnknownExecution","已清理或不存在的执行明确返回未知记录");
            Check((await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:"../scheduler.db"))).Code=="InvalidRequest","执行日志端点拒绝路径和非执行标识");
            var running=repository.Begin(plan,DateTimeOffset.UtcNow);
            Check((await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:running))).Execution?.Result=="Running","日志查询可读取进行中的持久快照");
            repository.End(running,plan,BackgroundJobResult.Success("days=8;sourceIssues=0;rows=25"));
            Check((await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:running))).Execution is {Result:"Succeeded",Checkpoint:"days=8;sourceIssues=0;rows=25"},"同一执行刷新后得到实际结束结果和摘要");
            for (var i = 0; i < 4; i++) { var connection = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous); await connection.ConnectAsync(1000); slow.Add(connection); }
            var watch = Stopwatch.StartNew(); var paused = await client.SendAsync(Request("Pause", revision: 1));
            Check(paused.Code == "Ok" && watch.Elapsed < TimeSpan.FromSeconds(3), "四个查询慢连接不阻塞管理员暂停");
            foreach (var connection in slow) connection.Dispose(); slow.Clear();
            var limited = false;
            for (var i = 0; i < 66; i++) limited |= (await client.QueryAsync("GetHealth")).Code == "RateLimited";
            Check(limited, "查询洪泛按真实 SID 限流");
            Check((await client.SendAsync(Request("Resume", revision: 2))).Code == "Ok", "只读额度耗尽不占用管理命令容量");
        }
        finally
        {
            foreach (var connection in slow) connection.Dispose(); stop.Cancel(); try { await listening; } catch (OperationCanceledException) { }
        }
    }
    private static async Task LifecycleAsync(string root)
    {
        var mixed = Package(root, ("crs.bad", "BadStartEntry"), ("crs.good", "GoodEntry"));
        await using (var catalog = new BackgroundPluginCatalog())
        {
            await catalog.LoadAsync(mixed.Root, mixed.Trust, default);
            Check(catalog.Jobs.Count == 2 && catalog.Statuses.Single(x => x.PluginId == "crs.bad").State == "Failed", "初始化失败插件隔离，正常插件继续注册");
        }
        var hanging = Package(root, ("crs.hanging", "HangingStartEntry")); var notified = false;
        var stuck = new BackgroundPluginCatalog(TimeSpan.FromMilliseconds(100), () => notified = true); var watch = Stopwatch.StartNew();
        try { await stuck.LoadAsync(hanging.Root, hanging.Trust, default); throw new InvalidOperationException("未检测出不合作插件。"); }
        catch (PluginLifecycleTimeoutException) { Check(notified && watch.Elapsed < TimeSpan.FromSeconds(3), "启动不响应取消时明确要求宿主终止，而非假装卸载"); }
        try{await stuck.DisposeAsync();throw new InvalidOperationException("失控插件仍被清理。");}
        catch(PluginLifecycleTimeoutException){Check(stuck.RequiresProcessRestart,"失控后禁止释放会话，保留需要进程重启状态");}
        var badStop = Package(root, ("crs.stop", "BadStopEntry"), ("crs.good", "GoodEntry"));
        var cleanup = new BackgroundPluginCatalog(); await cleanup.LoadAsync(badStop.Root, badStop.Trust, default);
        try { await cleanup.DisposeAsync(); throw new InvalidOperationException("停止故障未汇总。"); }
        catch (AggregateException error) { Check(error.InnerExceptions.Count == 1 && cleanup.Jobs.Count == 0, "逐插件清理继续完成并汇总停止异常"); }
    }
    private static async Task QueryIsolationAsync(string root, byte[] key)
    {
        var package = Package(root, ("crs.slow", "GoodEntry"), ("crs.fast", "GoodEntry"));
        await using var catalog = new BackgroundPluginCatalog(TimeSpan.FromSeconds(25));
        await catalog.LoadAsync(package.Root, package.Trust, default);
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "query-isolation.db"), key);
        await using var runtime = new BackgroundRuntime(repository, catalog);
        await runtime.StartAsync(default);
        BackgroundRequest Query(string plugin, string payload) => Request("QueryPlugin", plugin) with { Payload = payload };
        var entered = Path.Combine(root, "slow-query-entered");
        var slow = runtime.HandleAsync(Query("crs.slow", "slow:" + entered), Admin, default);
        await UntilAsync(() => Task.FromResult(File.Exists(entered)));
        var watch = Stopwatch.StartNew();
        var health = await runtime.HandleAsync(Request("GetHealth"), Admin, default).WaitAsync(TimeSpan.FromSeconds(2));
        var pause = await runtime.HandleAsync(Request("Pause", "crs.fast", revision: 1), Admin, default).WaitAsync(TimeSpan.FromSeconds(2));
        var fast = await runtime.HandleAsync(Query("crs.fast", "ok"), Admin, default).WaitAsync(TimeSpan.FromSeconds(2));
        Check(health.Code == "Ok" && pause.Code == "Ok" && fast.Code == "Ok" && !slow.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(2),
            "15秒插件查询期间健康、暂停及另一插件查询在2秒内完成");
        Check((await runtime.HandleAsync(Query("crs.slow", "ok"), Admin, default)).Code == "PluginBusy", "同插件查询有界准入，无全局等待队列");
        Check((await slow).Code == "Ok", "慢查询正常结束并释放插件额度");
        Check((await runtime.HandleAsync(Query("crs.slow", "fail"), Admin, default)).Code == "PluginQueryFailed"
            && (await runtime.HandleAsync(Request("GetHealth"), Admin, default)).Health?.State == "Running", "查询异常局限于插件，宿主仓储不误报故障");
        using var cancellation = new CancellationTokenSource();
        var cancelEntered = Path.Combine(root, "cancel-query-entered");
        var cancelled = runtime.HandleAsync(Query("crs.slow", "cancel:" + cancelEntered), Admin, cancellation.Token);
        await UntilAsync(() => Task.FromResult(File.Exists(cancelEntered)));
        cancellation.Cancel();
        try { await cancelled; throw new InvalidOperationException("查询未取消。"); }
        catch (OperationCanceledException) { Check(true, "调用方取消传递至插件"); }
        Check((await runtime.HandleAsync(Query("crs.slow", "ok"), Admin, default)).Code == "Ok", "取消及异常均释放准入额度");
        var stopEntered = Path.Combine(root, "stop-query-entered");
        var stopping = runtime.HandleAsync(Query("crs.slow", "cancel:" + stopEntered), Admin, default);
        await UntilAsync(() => Task.FromResult(File.Exists(stopEntered)));
        await runtime.EnterMaintenanceAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        try { await stopping; throw new InvalidOperationException("维护未取消查询。"); }
        catch (OperationCanceledException) { Check(true, "维护取消并排空查询，释放会话前无在途访问"); }
        Check((await runtime.HandleAsync(Query("crs.fast", "ok"), Admin, default)).Code == "Maintenance", "维护后拒绝新查询");
    }
    private static async Task ConcurrencyAsync(string root, BackgroundPluginCatalog catalog, byte[] key)
    {
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "concurrency.db"), key);
        await using var runtime = new BackgroundRuntime(repository, catalog); await runtime.StartAsync(default);
        var request = Request("RunNow"); var first = await runtime.HandleAsync(request, Admin, default);
        await runtime.HandleAsync(Request("RunNow", job: "two"), Admin, default);
        await UntilAsync(() => Task.FromResult(repository.History().Count(x => x.Result == "Succeeded") == 2));
        Check(repository.History().All(x => x.Checkpoint == "maximum=1"), "同一 Session 两个不同 JobKey 共用写入门槛");
        var repeated = await runtime.HandleAsync(request, Admin, default);
        Check(first.Code == "Accepted" && repeated.Code == "Succeeded" && repeated.Receipt?.ExecutionId is not null && repository.History().Count == 2, "完成后重发原请求返回原执行结果，不新增任务");
        Check((await runtime.HandleAsync(request with { JobId = "two" }, Admin, default)).Code == "InvalidOrStaleRequest", "原请求 ID 携带不同参数被拒绝");
    }
    private static async Task PendingRecoveryAsync(string root, (string Root, string Trust) package, string servicePackage)
    {
        var data = Path.Combine(root, "durable-process"); var pipe = "CRS.Worker.Recovery." + Guid.NewGuid().ToString("N");
        var hold = Path.Combine(root, "hold.fixture"); File.WriteAllText(hold, "仅测试进程阻塞夹具");
        Process Start()
        {
            var info = new ProcessStartInfo(Path.Combine(servicePackage, "CRS.Worker.Service.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var value in new[] { "--console", "--data", data, "--plugins", package.Root, "--trust", package.Trust, "--pipe", pipe }) info.ArgumentList.Add(value);
            info.Environment["CRS_WORKER_TEST_HOLD"] = hold;
            var process = Process.Start(info)!; process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { }; process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
        }
        var client = new BackgroundPipeClient(pipe); var firstRequest = Request("RunNow"); var pending = Request("RunNow", job: "two");
        using (var child = Start())
        {
            try
            {
                await client.QueryAsync("GetHealth"); await client.SendAsync(firstRequest);
                await UntilAsync(async () => (await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetCommand", RelatedRequestId: firstRequest.RequestId))).Receipt?.State == "Running");
                Check((await client.SendAsync(pending)).Code == "Accepted", "共享资源繁忙时先持久接受第二任务");
                Check((await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetCommand", RelatedRequestId: pending.RequestId))).Receipt?.State == "Pending", "强杀前确认命令已接受且尚未 Begin");
            }
            finally { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); }
        }
        File.Delete(hold);
        using var restored = Start();
        try
        {
            await client.QueryAsync("GetHealth");
            await UntilAsync(async () => (await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetCommand", RelatedRequestId: pending.RequestId))).Receipt?.State == "Completed");
            Check((await client.SendAsync(pending)).Code == "Succeeded", "真实强杀后恢复暂停计划的待执行命令，原请求可重发查询结果");
            var history = (await client.QueryAsync("GetHistory")).History!;
            Check(history.Count(x => x.JobId == "two") == 1 && history.Any(x => x.JobId == "one" && x.Result == "Interrupted"), "恢复采用至少一次执行，保留中断审计且未开始任务不重复");
        }
        finally { if (!restored.HasExited) restored.Kill(); await restored.WaitForExitAsync(); }
    }
    private sealed class AlternateProviderException():System.Data.Common.DbException("isolated alternate provider failure");
    private sealed class FaultRepository(ITaskRepository inner) : ITaskRepository
    {
        public void AppendProgress(string id,BackgroundProgress progress){Fail("Progress");inner.AppendProgress(id,progress);}
        public string? Failure { get; set; }
        public bool ProviderFailure {get;init;}
        private void Fail(string operation)
        {
            if(Failure==operation)
                throw ProviderFailure?new AlternateProviderException():new IOException("隔离持久化边界故障");
        }
        public void RegisterDefault(string p, BackgroundJobDescriptor d) => inner.RegisterDefault(p, d);
        public List<TaskDefinition> Plans() => inner.Plans();
        public void Save(TaskDefinition p, long r, Guid? id = null) => inner.Save(p, r, id);
        public void Reconciled(TaskDefinition p) => inner.Reconciled(p);
        public string Begin(TaskDefinition p, DateTimeOffset at, Guid? id = null) { Fail("Begin"); return inner.Begin(p, at, id); }
        public void End(string id, TaskDefinition p, BackgroundJobResult r, Guid? request = null) { Fail("End"); inner.End(id, p, r, request); }
        public List<ExecutionRecord> History() => inner.History();
        public ExecutionRecord? FindExecution(string id)=>inner.FindExecution(id);
        public (List<ExecutionRecord> Rows, string? NextCursor) HistoryPage(string? c, int n,string? plugin=null,string? job=null,string? resultFilter=null,DateOnly? date=null) => inner.HistoryPage(c, n,plugin,job,resultFilter,date);
        public void Audit(string s, Guid id, string c, string r) { Fail("Audit"); inner.Audit(s, id, c, r); }
        public CommandReceipt? FindCommand(Guid id, string s) => inner.FindCommand(id, s);
        public BackgroundRequest ReadCommand(Guid id)=>inner.ReadCommand(id);
        public CommandReceipt? ReadCommandState(Guid id)=>inner.ReadCommandState(id);
        public CommandReceipt AcceptCommand(BackgroundRequest r, string s) => inner.AcceptCommand(r, s);
        public void CompleteCommand(Guid id, string c) => inner.CompleteCommand(id, c);
        public List<(Guid RequestId, string PluginId, string JobId)> PendingRuns() => inner.PendingRuns();
        public void CancelPending(string p, string j) => inner.CancelPending(p, j);
    }
}
