using System.Security.Cryptography;
using CRS.Timing;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

internal static class PluginPlanCreationChecks
{
    private const string PluginId = "crs.test-plans";
    private const string Daily = "0 0 21 * * ?";

    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CRS.PluginPlans", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "task.db");
        var key = RandomNumberGenerator.GetBytes(32);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            using (var repository = new EncryptedTaskRepository(path, key))
            {
                await using var runtime = new BackgroundRuntime(repository, CreateCatalog());
                await runtime.StartAsync(stop.Token);
                var pipeName = "CRS.PluginPlans." + Guid.NewGuid().ToString("N");
                var server = new BackgroundPipeServer(runtime, pipeName).RunAsync(stop.Token);
                try
                {
                    var client = new BackgroundPipeClient(pipeName);
                    Task<BackgroundResponse> Read(string command, string? cursor = null) => client.SendAsync(
                        new(WorkerProtocol.Version, Guid.NewGuid(), command, Cursor: cursor, PageSize: 1), stop.Token);
                    BackgroundRequest Add(string jobId, string cron, string state) => new(
                        WorkerProtocol.Version, Guid.NewGuid(), WorkerProtocol.AddPluginPlan, PluginId, jobId,
                        Cron: cron, Payload: state);
                    var template = await Read(WorkerProtocol.GetPluginPlanOptions);
                    Check(template.Jobs is [{ JobId: "automatic", Revision: 0 }] && template.NextCursor is not null,
                        "待添加任务可发现且尚未注册计划");
                    var legacy = await Read(WorkerProtocol.GetPluginPlanOptions, template.NextCursor);
                    var paused = await Read(WorkerProtocol.GetPluginPlanOptions, legacy.NextCursor);
                    Check(legacy.Jobs is [{ JobId: "legacy", Revision: 1 }] && paused.Jobs is [{ JobId: "paused", Revision: 0 }]
                        && paused.NextCursor is null && (await Read("GetJobs")).Jobs is [{ JobId: "legacy" }],
                        "分页无重复，兼容自动注册并排除手动和诊断任务");

                    var add = Add("automatic", "0/1 * * * * ?", "Enabled");
                    var denied = await runtime.HandleAsync(add, new("read-only", false), stop.Token);
                    Check(denied.Code == "Forbidden", "未授权调用不能注册计划");
                    Check((await client.SendAsync(Add("invalid", Daily, "Enabled"), stop.Token)).Code == "JobUnavailable"
                        && (await client.SendAsync(Add("diagnostic", Daily, "Enabled"), stop.Token)).Code == "JobUnavailable"
                        && (await client.SendAsync(Add("manual", Daily, "Enabled"), stop.Token)).Code == "JobUnavailable"
                        && (await client.SendAsync(Add("automatic", "0 61 12 * * ?", "Enabled"), stop.Token)).Code == "InvalidOrStaleRequest",
                        "未知任务、非定时任务和非法频率均被拒绝");
                    var savedPaused = await client.SendAsync(Add("paused", Daily, "Paused"), stop.Token);
                    Check(savedPaused.Jobs is [{ Enabled: false, Revision: 1, PlanState: "Disabled", NextScheduledAt: null }],
                        "添加后可保持暂停");
                    var saved = await client.SendAsync(add, stop.Token);
                    Check(saved.Code == "Ok" && saved.Receipt is { State: "Completed", Code: "Ok" }
                        && saved.Jobs is [{ Enabled: true, Revision: 1, NextScheduledAt: not null, TimeZoneId: BeijingClock.ZoneId }],
                        "回执确认添加并返回下一次调度");
                    var duplicate = await client.SendAsync(Add("automatic", Daily, "Paused"), stop.Token);
                    var replay = await client.SendAsync(add, stop.Token);
                    var mismatch = await client.SendAsync(add with { Cron = Daily }, stop.Token);
                    Check(duplicate.Code == "PlanAlreadyExists" && replay.Receipt == saved.Receipt
                        && replay.Jobs is [{ Revision: 1, Enabled: true }] && mismatch.Code == "InvalidOrStaleRequest",
                        "重复添加与原命令重放均不覆盖已有配置");
                    BackgroundResponse history;
                    do
                    {
                        await Task.Delay(200, stop.Token);
                        history = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetHistoryByDate",
                            PluginId: PluginId, JobId: "automatic", Payload: BeijingClock.Today.ToString("yyyy-MM-dd")), stop.Token);
                    } while (history.History?.Any(row => row.Result == "Succeeded") != true);
                    Check(repository.History().All(row => row.JobId == "automatic"), "定时任务写入真实历史，暂停和诊断任务未运行");
                    var pause = new BackgroundRequest(WorkerProtocol.Version, Guid.NewGuid(), "Pause", PluginId, "automatic",
                        ExpectedRevision: 1);
                    Check((await client.SendAsync(pause, stop.Token)).Code == "Ok", "新计划可沿用现有暂停入口");
                }
                finally
                {
                    stop.Cancel();
                    try { await server; }
                    catch (OperationCanceledException) { }
                }
            }
            using var reopened = new EncryptedTaskRepository(path, key);
            await using var restarted = new BackgroundRuntime(reopened, CreateCatalog());
            await restarted.StartAsync(default);
            var plan = reopened.Plans().Single(row => row.JobId == "automatic");
            Check(plan is { Enabled: false, Revision: 2, Cron: "0/1 * * * * ?" }
                && reopened.History().Any(row => row.JobId == "automatic" && row.Result == "Succeeded"),
                "重启保留计划、修订号、启停状态和历史");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, true);
        }
    }

    private static BackgroundPluginCatalog CreateCatalog()
    {
        var catalog = new BackgroundPluginCatalog();
        var session = new Session();
        ((List<HostedBackgroundJob>)catalog.Jobs).AddRange(
            session.DescribeJobs().Select(descriptor => new HostedBackgroundJob(PluginId, descriptor, session)));
        return catalog;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS 插件计划：" + message);
    }

    private sealed class Session : IBackgroundPluginSession
    {
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs() =>
        [
            new("automatic", "定时夹具", Daily, BeijingClock.ZoneId, true) { RequiresPlanCreation = true },
            new("legacy", "现有计划", Daily, BeijingClock.ZoneId, false),
            new("paused", "暂停夹具", Daily, BeijingClock.ZoneId, true) { RequiresPlanCreation = true },
            new("manual", "手动夹具", Daily, BeijingClock.ZoneId, false, ManualOnly: true),
            new("diagnostic", "诊断夹具", Daily, BeijingClock.ZoneId, false) { Diagnostic = true }
        ];
        public Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken token) =>
            Task.FromResult(BackgroundJobResult.Success());
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
