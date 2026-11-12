using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using CRS.Worker.Ipc;

namespace CRS.Tests.Background.Gateway;

/// <summary>仅供隔离验收使用，由 SCM 以真实 Gateway 虚拟账户启动。</summary>
internal sealed class AiGatewayScmProbe : ServiceBase
{
    private readonly string pipe;
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
    private Task? probe;

    public AiGatewayScmProbe(string pipe)
    {
        const string prefix = "CRS.AiGatewayBoundary.";
        if (!pipe.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipe.AsSpan(prefix.Length), "N", out _))
            throw new ArgumentException("验收服务只允许使用随机命名的测试管道。", nameof(pipe));
        this.pipe = pipe;
        ServiceName = AiGatewayWorkerEndpoint.ServiceName;
        AutoLog = false;
    }

    protected override void OnStart(string[] args) => probe = RunAsync(stop.Token);

    protected override void OnStop()
    {
        stop.Cancel();
        try { probe?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) stop.Dispose();
        base.Dispose(disposing);
    }

    private async Task RunAsync(CancellationToken token)
    {
        string result;
        try { result = await CheckAsync(token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            result = error.GetType().Name;
        }
        await using var receipt = NewClient(pipe + AiGatewayPipeChecks.ResultSuffix);
        await receipt.ConnectAsync(token);
        await Framing.WriteAsync(receipt, result, token);
    }

    private async Task<string> CheckAsync(CancellationToken token)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != AiGatewayWorkerEndpoint.ServiceSid) return "WrongServiceAccount";

        var overview = AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Overview);
        var aiPipe = pipe + AiGatewayWorkerEndpoint.PipeSuffix;
        if ((await SendAsync(aiPipe, overview, token)).Code != "Ok" ||
            (await SendAsync(aiPipe, AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Coverage), token)).Code != "Ok")
            return "PublicReadDenied";

        // 直接发原始 IPC 请求，绕过 AI 客户端校验，验证 Worker 自己的白名单。
        var rejected = new[]
        {
            overview with { Command = "RunNow" },
            overview with { Command = "GetEmailSettings" },
            overview with { Command = "GetPluginDocument" },
            overview with { PluginId = "crs.other" },
            overview with { JobId = "review" },
            AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Overview.Replace("overview", "release-query")),
            AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Overview.Replace("overview", "export")),
            AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Overview.Replace("overview", "research-submit")),
            AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Overview.Replace("overview", "ai-public-overview")),
            AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Overview.Replace("\"Offset\":0", "\"Offset\":1,\"Snapshot\":\"desktop-snapshot\"")),
            AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Overview.Replace("\"Count\":10", "\"Count\":65")),
            AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Overview.Replace("\"Count\":10", "\"Count\":10,\"Count\":9")),
            AiGatewayPipeChecks.Query(AiGatewayPipeChecks.Coverage.Replace("2026-11-06", "2027-01-01"))
        };
        foreach (var request in rejected)
            if ((await SendAsync(aiPipe, request, token)).Code != "Forbidden") return "OutOfScopeAccepted";

        foreach (var name in new[] { pipe, pipe + BackgroundPipeServer.ControlSuffix })
        {
            if (!await AiGatewayPipeChecks.RejectAsync(() => SendAsync(name, overview, token)))
                return "DesktopOrControlAccepted";
        }
        return "Ok";
    }

    internal static async Task<BackgroundResponse> SendAsync(string name, BackgroundRequest request, CancellationToken token)
    {
        await using var client = NewClient(name);
        await client.ConnectAsync(token);
        await Framing.WriteAsync(client, request, token);
        return await Framing.ReadAsync<BackgroundResponse>(client, token);
    }

    private static NamedPipeClientStream NewClient(string name) =>
        new(".", name, AiGatewayWorkerEndpoint.ClientRights, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation, HandleInheritability.None);
}
