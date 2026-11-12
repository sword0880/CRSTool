using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Gateway.Authorization;
using CRS.AI.Gateway.Transport;
using CRS.AI.Infrastructure;
using CRS.AI.Query;
using CRS.Tests.AiQuery.Fixtures;

namespace CRS.Tests.AiQuery.Acceptance;

internal static class LocalGatewayAcceptance
{
    public static async Task RunAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("本地 Gateway 管理管道验收需要管理员终端。");
        var sid = identity.User!.Value;
        var root = Path.Combine(Path.GetTempPath(), "CRS-Gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var grants = Path.Combine(root, "subjects.json");
        var audit = Path.Combine(root, "events.jsonl");
        await File.WriteAllTextAsync(grants, "[]");
        await File.WriteAllTextAsync(audit, "");
        var name = "CRS.AI.Gateway.Test." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var stop = new CancellationTokenSource();
        using var access = new LocalAuthorizations(new AuthorizationFile(grants));
        var worker = new FakeWorker();
        var gateway = AiReadGatewayFactory.Create(worker, new AiAppendOnlyFileAuditSink(audit));
        var running = new LocalGatewayServer(gateway, access, name).RunAsync(stop.Token);
        try
        {
            var client = new LocalGatewayClient(name, verifyService: false);
            var token = deadline.Token;
            Task<LocalReadResponse> CallAsync() => client.CallAsync(new(LocalGatewayProtocol.Version,
                Guid.NewGuid(), AiCatalog.Overview, JsonSerializer.SerializeToElement(new { })), token);
            Task<LocalAccessResponse> SetAsync(string action) => client.SetAccessAsync(new(LocalGatewayProtocol.Version,
                Guid.NewGuid(), action, sid), token);

            Check((await CallAsync()).Result.Code == "Unauthorized" && worker.Calls == 0,
                "空授权表默认拒绝，管理员查询同样需要显式授权");
            await CheckDelayedReplyAsync(name, token);
            Check(await RejectIdentityClaimAsync(name, sid, token) && worker.Calls == 0,
                "请求声明的身份字段被拒绝");
            Check((await SetAsync("Grant")).Code == "Ok" &&
                  new AuthorizationFile(grants).Load().SequenceEqual([sid]), "管理员授权已持久化");
            Check((await CallAsync()).Result.Status == "Partial" && worker.Calls == 1,
                "实际 Windows 令牌获授权后通过共享审计和 Worker");

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new TaskCompletionSource<WorkerReadReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            worker.Handler = async (_, _, cancellation) =>
            {
                using var registration = cancellation.Register(() => cancelled.TrySetResult());
                started.SetResult();
                // 模拟上游收到取消后仍返回数据，验证最后一道释放校验。
                return await delayed.Task.WaitAsync(token);
            };
            var pending = CallAsync();
            await started.Task.WaitAsync(token);
            Check((await SetAsync("Revoke")).Code == "Ok", "撤销命令成功");
            await cancelled.Task.WaitAsync(token);
            Check((await CallAsync()).Result.Code == "Unauthorized" && worker.Calls == 2,
                "撤销取消进行中的查询，并阻止新请求");
            Check((await SetAsync("Grant")).Code == "Ok", "撤销后允许显式重新授权");
            delayed.SetResult(FakeWorker.Page("[]"));
            var late = (await pending).Result;
            Check(late.Code == "Unauthorized" && late.Data is null && late.DataVersion is null,
                "重新授权不能恢复已撤销的在途结果");
            Check((await SetAsync("Revoke")).Code == "Ok", "再次撤销成功");
            using var reloaded = new LocalAuthorizations(new AuthorizationFile(grants));
            using var persisted = await reloaded.AcquireAsync(sid, token);
            Check(persisted is null, "重新加载后保留撤销状态");
            var events = await File.ReadAllLinesAsync(audit, token);
            Check(events.Length == 4 && events.All(line => !line.Contains(sid, StringComparison.Ordinal)),
                "两次通过准入的调用写入四条脱敏记录，越权流量不消耗审计容量");
        }
        finally
        {
            stop.Cancel();
            try { await running; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            File.Delete(grants);
            File.Delete(audit);
            Directory.Delete(root);
        }
    }

    private static async Task<bool> RejectIdentityClaimAsync(string name, string sid, CancellationToken token)
    {
        await using var pipe = new NamedPipeClientStream(".", name, LocalGatewayProtocol.ClientRights,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(token);
        await WriteFrameAsync(pipe, new
        {
            Version = LocalGatewayProtocol.Version,
            RequestId = Guid.NewGuid(),
            Tool = AiCatalog.Overview,
            Arguments = new { },
            CallerSid = sid,
            PublicResearchAuthorized = true
        }, token);
        return await pipe.ReadAsync(new byte[1], token) == 0;
    }

    private static async Task CheckDelayedReplyAsync(string name, CancellationToken token)
    {
        await using var pipe = new NamedPipeClientStream(".", name + LocalGatewayProtocol.ControlSuffix,
            LocalGatewayProtocol.ClientRights, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(token);
        var request = new LocalAccessRequest(LocalGatewayProtocol.Version, Guid.NewGuid(), "Status");
        await WriteFrameAsync(pipe, request, token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), token);
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Check(length is >= 2 and <= 1024, "慢速客户端收到完整响应帧头");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, token);
        var reply = JsonSerializer.Deserialize<LocalAccessResponse>(body);
        Check(reply?.RequestId == request.RequestId && reply.Code == "Ok", "客户端延迟读取时保留授权管理响应");
    }

    private static async Task WriteFrameAsync<T>(Stream pipe, T payload, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await pipe.WriteAsync(header, token);
        await pipe.WriteAsync(body, token);
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS 本地 Gateway " + label);
    }
}
