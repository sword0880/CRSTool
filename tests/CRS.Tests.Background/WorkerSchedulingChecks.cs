using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using TaskActivityTests;

/// <summary>真实Quartz与多Job插件验证吞吐隔离；所有阻塞点由测试目录文件控制。</summary>
internal static class WorkerSchedulingChecks
{
    private static int passed;
    private static readonly BackgroundCaller Admin = new(WindowsIdentity.GetCurrent().User!.Value, true);
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS 调度：" + label); }
    private static BackgroundRequest Request(string command, string plugin, string job = "one", long? revision = null) =>
        new(WorkerProtocol.Version, Guid.NewGuid(), command, plugin, job, ExpectedRevision: revision);
    private static async Task Until(Func<Task<bool>> predicate, int seconds = 10)
    {
        var deadline = Stopwatch.StartNew();
        while (!await predicate()) { if (deadline.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("调度验收等待超时。"); await Task.Delay(25); }
    }
    public static async Task RunAsync(string root)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try { Settings(root); await Isolation(root, key); await Parallel(root, key); await EightPlugins(root,key);await GroupIsolation(root,key); await DeferredCommand(root,key); await ManualImmediateChecks.RunAsync(root,key); }
        finally { CryptographicOperations.ZeroMemory(key); }
        Console.WriteLine($"Worker公平调度通过：{passed}项。");
    }
    private static async Task GroupIsolation(string root,byte[] key)
    {
        var package=WorkerRemediationChecks.Package(root,("crs.grouped","GroupedEntry"),("crs.bad-group","InvalidGroupEntry"));
        await using var catalog=new BackgroundPluginCatalog();await catalog.LoadAsync(package.Root,package.Trust,default);
        Check(catalog.Statuses.Single(x=>x.PluginId=="crs.bad-group").State=="Failed","同执行组声明不一致只隔离该插件");
        using var repository=new EncryptedTaskRepository(Path.Combine(root,"groups.db"),key);
        await using var runtime=new BackgroundRuntime(repository,catalog);
        var hold=Path.Combine(root,"group-hold");File.WriteAllText(hold,"synthetic");
        var old=Environment.GetEnvironmentVariable("CRS_WORKER_TEST_HOLD");Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD",hold);
        try
        {
            await runtime.StartAsync(default);
            foreach(var job in new[]{"collect1","collect2","collect3"})await runtime.HandleAsync(Request("RunNow","crs.grouped",job),Admin,default);
            await Until(async()=>(await runtime.HandleAsync(Request("GetHealth","crs.grouped"),Admin,default)).Health?.Scheduling is{Running:2,Waiting:1});
            Check(repository.History().Count(x=>x.Result=="Running")==2,"长采集组只占两个名额，第三个在宿主等待");
            await runtime.HandleAsync(Request("RunNow","crs.grouped","evaluation"),Admin,default);
            await runtime.HandleAsync(Request("RunNow","crs.grouped","management"),Admin,default);
            await Until(()=>Task.FromResult(repository.History().Count(x=>x.Result=="Succeeded")==2),2);
            Check(File.Exists(hold)&&repository.History().Count(x=>x.Result=="Running")==2,
                "队头第三个采集不阻塞后来的预警和管理任务，两者在长采集未结束时真实完成");
            File.Delete(hold);
            await Until(()=>Task.FromResult(repository.History().Count(x=>x.Result=="Succeeded")==5));
            await Until(async()=>(await runtime.HandleAsync(Request("GetHealth","crs.grouped"),Admin,default)).Health?.Scheduling is{Running:0,Waiting:0});
            Check(repository.PendingRuns().Count==0,"执行组额度释放后等待采集全部完成，不遗留额度或命令");
        }
        finally{File.Delete(hold);Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD",old);}
    }
    private static async Task EightPlugins(string root,byte[] key)
    {
        var plugins=Enumerable.Range(1,9).Select(i=>(Id:"crs.concurrent"+i,Entry:"GoodEntry")).ToArray();
        var package=WorkerRemediationChecks.Package(root,plugins);
        await using var catalog=new BackgroundPluginCatalog();await catalog.LoadAsync(package.Root,package.Trust,default);
        using var repository=new EncryptedTaskRepository(Path.Combine(root,"eight-plugins.db"),key);
        await using var runtime=new BackgroundRuntime(repository,catalog);
        var hold=Path.Combine(root,"eight-plugins-hold");File.WriteAllText(hold,"synthetic");
        var previous=Environment.GetEnvironmentVariable("CRS_WORKER_TEST_HOLD");Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD",hold);
        try
        {
            await runtime.StartAsync(default);
            foreach(var plugin in plugins.Take(8))
                Check((await runtime.HandleAsync(Request("RunNow",plugin.Id),Admin,default)).Code=="Accepted","不同插件的独立任务接受并发运行");
            await Until(()=>Task.FromResult(repository.History().Count(x=>x.Result=="Running")==8));
            var health=await runtime.HandleAsync(Request("GetHealth",plugins[0].Id),Admin,default);
            Check(health.Health?.Scheduling is{MaxConcurrency:8,Running:8}&&health.Health.Scheduling.Plugins!.Count(x=>x.Running==1)==8,
                "默认8个全局名额被8个不同任务实际同时占用");
            await runtime.HandleAsync(Request("RunNow",plugins[8].Id),Admin,default);
            Check(repository.History().Count(x=>x.Result=="Running")==8&&repository.PendingRuns().Count==1,
                "第9个任务持久等待，不突破全局并发8的上限");
            File.Delete(hold);
            await Until(()=>Task.FromResult(repository.History().Count(x=>x.Result=="Succeeded")==9));
            Check(repository.PendingRuns().Count==0,"释放名额后第9个任务也真实完成，无饥饿或漏执行");
        }
        finally{File.Delete(hold);Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD",previous);}
    }
    private static async Task DeferredCommand(string root,byte[] key)
    {
        var package=WorkerRemediationChecks.Package(root,("crs.deferred","DeferredEntry"));var path=Path.Combine(root,"deferred-command.db");var hold=Path.Combine(root,"defer.fixture");File.WriteAllText(hold,"synthetic");
        var old=Environment.GetEnvironmentVariable("CRS_WORKER_TEST_DEFER");Environment.SetEnvironmentVariable("CRS_WORKER_TEST_DEFER",hold);
        var request=Request("RunNow","crs.deferred") with{Payload="synthetic-historical-range"};
        try
        {
            await using(var catalog=new BackgroundPluginCatalog())
            {
                await catalog.LoadAsync(package.Root,package.Trust,default);using var repository=new EncryptedTaskRepository(path,key);await using var runtime=new BackgroundRuntime(repository,catalog);await runtime.StartAsync(default);
                await runtime.HandleAsync(request,Admin,default);
                await Until(()=>Task.FromResult(repository.FindCommand(request.RequestId,Admin.Sid) is{State:"Pending",Code:"RetryDeferred"}));
                Check(repository.PendingRuns().Single().RequestId==request.RequestId,"延后重试保留同一持久命令及原参数");
            }
            File.Delete(hold);
            await using(var catalog=new BackgroundPluginCatalog())
            {
                await catalog.LoadAsync(package.Root,package.Trust,default);using var repository=new EncryptedTaskRepository(path,key);await using var runtime=new BackgroundRuntime(repository,catalog);await runtime.StartAsync(default);
                await Until(()=>Task.FromResult(repository.FindCommand(request.RequestId,Admin.Sid) is{State:"Completed",Code:"Succeeded"}),40);
                Check(repository.History().Any(x=>x.Result=="Succeeded"&&x.Checkpoint=="originalPayload=Preserved"),"服务重建后仍用原历史参数执行手动专用任务");
            }
        }
        finally{Environment.SetEnvironmentVariable("CRS_WORKER_TEST_DEFER",old);}
    }
    private static void Settings(string root)
    {
        var settingsRoot = Path.Combine(root, "scheduling-settings"); Directory.CreateDirectory(settingsRoot);
        Check(WorkerExecutionSettings.Read(settingsRoot).EffectiveMaxConcurrency == 8, "缺省并发固定为8");
        var file = Path.Combine(settingsRoot, "worker-settings.json");
        File.WriteAllText(file, """{"maxConcurrency":8,"maxPendingPerPlugin":4}""");
        Check(WorkerExecutionSettings.Read(settingsRoot) is { MaxConcurrency: 8, MaxPendingPerPlugin: 4 }, "受限运行配置可指定并发及插件等待预算");
        foreach (var invalid in new[] { """{"maxConcurrency":33}""", """{"maxPendingPerPlugin":0}""", """{"unknown":4}""" })
        {
            File.WriteAllText(file, invalid);
            try { WorkerExecutionSettings.Read(settingsRoot); throw new Exception("配置未拒绝。"); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { Check(true, "无效或未知并发配置明确拒绝"); }
        }
    }
    private static async Task Isolation(string root, byte[] key)
    {
        var package = WorkerRemediationChecks.Package(root, ("crs.slow", "FloodEntry"), ("crs.fast", "GoodEntry"));
        await using var catalog = new BackgroundPluginCatalog(); await catalog.LoadAsync(package.Root, package.Trust, default);
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "scheduling-isolation.db"), key);
        await using var runtime = new BackgroundRuntime(repository, catalog, executionSettings: new(4, 4));
        var hold = Path.Combine(root, "scheduling-hold"); File.WriteAllText(hold, "synthetic");
        var previousHold = Environment.GetEnvironmentVariable("CRS_WORKER_TEST_HOLD");
        Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD", hold);
        Task<BackgroundResponse> Send(BackgroundRequest request) => runtime.HandleAsync(request, Admin, default);
        Task<BackgroundResponse> Health() => Send(Request("GetHealth", "crs.slow"));
        try
        {
            await runtime.StartAsync(default);
            var firstRequest=Request("RunNow","crs.slow");
            Check((await Send(firstRequest)).Code == "Accepted", "慢插件首任务正常启动");
            await Until(async () => (await Health()).Health?.Scheduling?.Running == 1);
            Check((await Send(firstRequest)).Code=="Accepted"&&repository.FindCommand(firstRequest.RequestId,Admin.Sid)?.State is "Pending" or "Running","运行中的原请求重放保留未完成回执，不提前完成命令");
            var queued = new List<BackgroundRequest>();
            foreach (var job in new[] { "two", "job3", "job4", "job5" })
            {
                var request = Request("RunNow", "crs.slow", job); queued.Add(request);
                Check((await Send(request)).Code == "Accepted", "不同Job持久接受并进入有界等待");
                var expected = queued.Count;
                await Until(async () => (await Health()).Health?.Scheduling?.Waiting == expected);
            }
            var overload = Request("RunNow", "crs.slow", "job6");
            Check((await Send(overload)).Code == "PluginBusy" && repository.FindCommand(overload.RequestId, Admin.Sid) is null, "超过插件预算的手动请求立即繁忙，不落盘制造积压");
            var watch = Stopwatch.StartNew();
            await Send(Request("RunNow", "crs.fast", "two"));
            await Until(() => Task.FromResult(repository.History().Any(x => x.PluginId == "crs.fast" && x.Result == "Succeeded")), 2);
            Check(watch.Elapsed < TimeSpan.FromSeconds(2) && File.Exists(hold), "一个慢插件和四个等待Job不占满4个Quartz名额，另一插件2秒内完成");
            await Send(Request("Resume", "crs.slow", "two", 1));
            await Send(Request("ChangeSchedule", "crs.slow", "two", 2) with { Cron = "0/1 * * * * ?", TimeZoneId = "China Standard Time" });
            await Until(async () => (await Health()).Health?.Scheduling?.CoalescedTriggers >= 2);
            Check((await Health()).Health?.Scheduling is { Running: 1, Waiting: 4 }, "每秒重复触发合并成同Job单一等待项，无界积压被阻断");
            await Send(Request("ChangeSchedule", "crs.slow", "two", 3) with { Cron = "0 0 0 1 1 ?", TimeZoneId = "China Standard Time" });
            await Send(Request("Pause", "crs.slow", "job4", 1));
            Check(repository.FindCommand(queued[2].RequestId, Admin.Sid)?.Code == "Cancelled", "暂停移除等待任务并完成原持久命令取消");
            Check((await Send(overload)).Code == "Accepted" && (await Send(overload)).Code == "Accepted", "释放等待容量后可用原请求重试，重复接受不产生第二份工作");
            File.Delete(hold);
            await Until(() => Task.FromResult(repository.History().Count(x => x.PluginId == "crs.slow" && x.Result == "Succeeded") == 5));
            var slow = repository.History().Where(x => x.PluginId == "crs.slow").OrderBy(x => x.StartedUtc).ToArray();
            Check(slow.Select(x => x.JobId).SequenceEqual(new[] { "one", "two", "job3", "job5", "job6" }), "插件内按等待顺序完成，暂停项不执行，幂等请求只执行一次");
            Check(slow.All(x => x.Checkpoint == "maximum=1"), "未声明线程安全的共享会话保持单写保护");
            Check(slow[1].ScheduledUtc < slow[0].EndedUtc && slow[1].StartedUtc >= slow[0].EndedUtc, "唤醒后保留原计划时间，可从开始时间计算真实等待");
            await Until(async () => (await Health()).Health?.Scheduling is { Running: 0, Waiting: 0 });
            Check(true, "等待项最终排空，不遗留占用或重复唤醒");
        }
        finally { File.Delete(hold); Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD", previousHold); }
    }
    private static async Task Parallel(string root, byte[] key)
    {
        var package = WorkerRemediationChecks.Package(root, ("crs.parallel", "ParallelEntry"), ("crs.invalid", "InvalidConcurrencyEntry"));
        await using var catalog = new BackgroundPluginCatalog(); await catalog.LoadAsync(package.Root, package.Trust, default);
        Check(catalog.Statuses.Single(x => x.PluginId == "crs.invalid").State == "Failed", "不合法并发声明只隔离该插件");
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "scheduling-parallel.db"), key);
        await using var runtime = new BackgroundRuntime(repository, catalog, executionSettings: new(4, 4));
        var hold = Path.Combine(root, "parallel-hold"); File.WriteAllText(hold, "synthetic");
        var previousHold = Environment.GetEnvironmentVariable("CRS_WORKER_TEST_HOLD"); Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD", hold);
        try
        {
            await runtime.StartAsync(default);
            await runtime.HandleAsync(Request("RunNow", "crs.parallel"), Admin, default);
            await runtime.HandleAsync(Request("RunNow", "crs.parallel", "two"), Admin, default);
            await Until(async () => (await runtime.HandleAsync(Request("GetHealth", "crs.parallel"), Admin, default)).Health?.Scheduling?.Running == 2);
            Check(File.Exists(hold), "明确声明线程安全的同插件两个Job真实并行执行");
            File.Delete(hold);
            await Until(() => Task.FromResult(repository.History().Count(x => x.Result == "Succeeded") == 2));
            Check(repository.History().Any(x => x.Checkpoint == "maximum=2"), "并行夹具实际观察到两个重叠执行，而非仅返回接受");
        }
        finally { File.Delete(hold); Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD", previousHold); }
    }
}
