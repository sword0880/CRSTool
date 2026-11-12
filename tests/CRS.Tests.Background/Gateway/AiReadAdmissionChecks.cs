using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

namespace CRS.Tests.Background.Gateway;

internal static class AiReadAdmissionChecks
{
    public static async Task RunAsync()
    {
        foreach (var isolated in new[] { false, true })
            await RunAsync(isolated);
    }

    private static async Task RunAsync(bool isolated)
    {
        var root = Path.Combine(Path.GetTempPath(), "CRS.AiAdmission", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string plugin = AiGatewayWorkerRequestPolicy.PluginId;
        var package = WorkerRemediationChecks.Package(root, (plugin, "QueryWorkloadEntry"));
        var runner = Path.Combine(AppContext.BaseDirectory, "Runner", "CRS.Worker.PluginRunner.exe");
        await using var catalog = new BackgroundPluginCatalog(processSessions: isolated
            ? p => new IsolatedPluginSession(runner, package.Root, package.Trust, p) : null);
        await catalog.LoadAsync(package.Root, package.Trust, default);
        var key = RandomNumberGenerator.GetBytes(32);
        using var repository = new EncryptedTaskRepository(Path.Combine(root, "scheduler.db"), key);
        CryptographicOperations.ZeroMemory(key);
        await using var runtime = new BackgroundRuntime(repository, catalog);
        await runtime.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var desktop = new BackgroundCaller("fixture-desktop", false);
        var gateway = new BackgroundCaller(AiGatewayWorkerEndpoint.ServiceSid, false);

        Task<BackgroundResponse> Query(BackgroundCaller caller, string operation, string? marker = null,
            CancellationToken? requestToken = null) =>
            runtime.HandleAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "QueryPlugin", plugin,
                Payload: JsonSerializer.Serialize(new { Operation = operation, Marker = marker, Block = marker is not null })),
                caller, requestToken ?? token);

        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            Console.WriteLine($"PASS AI 读取准入（{(isolated ? "Runner" : "进程内")}）{message}");
        }

        Check((await Query(desktop, "ai-public-overview")).Code == "Forbidden" &&
              (await Query(gateway, "research")).Code == "Forbidden", "读取名额绑定受信管道路由与实际身份");
        using (var desktopCancel = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var priorityMarker = Path.Combine(root, "desktop-first");
            var preferred = Query(desktop, "research", priorityMarker, desktopCancel.Token);
            await WaitForMarkerAsync(priorityMarker, token);
            var blockedAi = Path.Combine(root, "ai-must-not-enter");
            Check((await Query(gateway, "ai-public-coverage", blockedAi).WaitAsync(TimeSpan.FromSeconds(2)))
                      .Code == "PluginBusy" && !File.Exists(blockedAi), "桌面有研究读时拒绝新的 AI 查询");
            desktopCancel.Cancel();
            try { await preferred; } catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        var aiMarker = Path.Combine(root, "ai-entered");
        var ai = Query(gateway, "ai-public-overview", aiMarker);
        await WaitForMarkerAsync(aiMarker, token);
        Check((await Query(gateway, "ai-public-coverage").WaitAsync(TimeSpan.FromSeconds(2))).Code == "PluginBusy",
            "第二个 AI 请求立即拒绝，无排队");
        Check((await Query(desktop, "research").WaitAsync(TimeSpan.FromSeconds(2))).Code == "Ok" && !ai.IsCompleted,
            "AI 在途时桌面研究读可立即完成");

        var desktopMarker = Path.Combine(root, "desktop-entered");
        var front = Query(desktop, "research", desktopMarker);
        await WaitForMarkerAsync(desktopMarker, token);
        // 保留 AI 阻塞调用，先核对维护会统一取消两个独立名额。
        await runtime.EnterMaintenanceAsync(token);
        foreach (var pending in new[] { ai, front })
        {
            try { await pending; throw new InvalidOperationException("维护未取消在途读取。"); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        Check(true, "维护会同时取消并排空 AI 和桌面读取");
    }

    private static async Task WaitForMarkerAsync(string path, CancellationToken token)
    {
        while (!File.Exists(path)) await Task.Delay(20, token);
    }
}
