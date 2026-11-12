using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using CRS.Worker.Ipc;

namespace CRS.Tests.Background.Gateway;

internal static class AiGatewayPipeChecks
{
    internal const string ResultSuffix = ".receipt";
    internal const string Overview = """{"Operation":"overview","Offset":0,"Count":10,"ContractVersion":1}""";
    internal const string Coverage = """
        {"Operation":"coverage","Market":"SSE","Ticker":"510300","From":"2026-11-02","To":"2026-11-06","Offset":0,"Count":10,"ContractVersion":1}
        """;

    public static async Task RunAsync(bool useScm)
    {
        var pipe = "CRS.AiGatewayBoundary." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var control = new RecordingControl();
        var running = new BackgroundPipeServer(control, pipe).RunAsync(stop.Token, enableAiGateway: true);
        try
        {
            var desktop = await new BackgroundPipeClient(pipe).QueryAsync("GetHealth", deadline.Token);
            Check(desktop.Code == "Ok", "桌面管道仍可访问原查询");

            // 当前用户拥有测试管道，故意绕过 ACL，验证连接后仍从令牌拒绝非 Gateway 身份。
            var ownerRejected = await RejectAsync(
                () => AiGatewayScmProbe.SendAsync(pipe + AiGatewayWorkerEndpoint.PipeSuffix,
                    Query(Overview), deadline.Token));
            Check(ownerRejected && control.Requests.Count == 1, "管道所有者也不能冒用 Gateway 身份调用 Worker");
            if (!useScm) return;

            await RunScmAsync(pipe, deadline.Token);
            var accepted = control.Requests.ToArray();
            Check(accepted.Length == 3 && accepted.Skip(1).All(x =>
                    x.Caller.Sid == AiGatewayWorkerEndpoint.ServiceSid && !x.Caller.CanManage) &&
                Operation(accepted[1].Request) == "ai-public-overview" &&
                Operation(accepted[2].Request) == "ai-public-coverage",
                "专用身份仅能分派两项无缓存的公开 ETF 查询");
        }
        finally
        {
            stop.Cancel();
            try { await running; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task RunScmAsync(string pipe, CancellationToken token)
    {
        var app = Path.Combine(AppContext.BaseDirectory, "CRS.Tests.Background.exe");
        if (!File.Exists(app)) throw new FileNotFoundException("SCM 验收需要测试项目生成的 Windows 可执行文件。", app);
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException();
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(owner);
        acl.AddAccessRule(new(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(AiGatewayWorkerEndpoint.Identity, AiGatewayWorkerEndpoint.ClientRights, AccessControlType.Allow));
        await using var receipt = NamedPipeServerStreamAcl.Create(pipe + ResultSuffix,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            0, 0, acl);

        const string serviceName = AiGatewayWorkerEndpoint.ServiceName;
        // create 失败时不接管已有服务。测试仅连接随机命名的合成 Worker 管道。
        await ScAsync(token, "create", serviceName, "binPath=", $"\"{app}\" --ai-gateway-worker-scm-probe {pipe}",
            "start=", "demand", "obj=", $"NT SERVICE\\{serviceName}");
        try
        {
            await ScAsync(token, "start", serviceName);
            await receipt.WaitForConnectionAsync(token);
            var result = await Framing.ReadAsync<string>(receipt, token);
            Check(result == "Ok", "SCM 虚拟账户通过专用管道且普通查询、管理管道均拒绝该账户（" + result + "）");
        }
        finally
        {
            using var service = new ServiceController(serviceName);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
                    service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            finally
            {
                service.Close();
                await ScAsync(cleanup.Token, "delete", serviceName);
            }
        }
    }

    private static async Task ScAsync(CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 SCM 验收命令。");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
            throw new IOException($"SCM {arguments[0]} 失败（{process.ExitCode}）。{await output}{await error}");
    }

    internal static BackgroundRequest Query(string payload) => new(WorkerProtocol.Version, Guid.NewGuid(), "QueryPlugin",
        PluginId: AiGatewayWorkerRequestPolicy.PluginId, Payload: payload);

    private static string? Operation(BackgroundRequest request)
    {
        using var document = JsonDocument.Parse(request.Payload!);
        return document.RootElement.GetProperty("Operation").GetString();
    }

    internal static async Task<bool> RejectAsync(Func<Task<BackgroundResponse>> send)
    {
        try { return (await send()).Code == "Forbidden"; }
        catch (Exception error) when (error is UnauthorizedAccessException or PipeFrameInterruptedException)
        {
            return true;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS AI Worker 管道 " + name);
    }

    private sealed class RecordingControl : IBackgroundControl
    {
        public System.Collections.Concurrent.ConcurrentQueue<(BackgroundRequest Request, BackgroundCaller Caller)> Requests { get; } = new();

        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token)
        {
            Requests.Enqueue((request, caller));
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version, request.RequestId, "Ok"));
        }
    }
}
