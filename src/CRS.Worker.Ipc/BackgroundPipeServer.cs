using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
namespace CRS.Worker.Ipc;

/// <summary>查询与管理使用独立容量和 ACL，普通用户的慢连接不能占用管理员控制通道。</summary>
public sealed class BackgroundPipeServer(IBackgroundControl control, string pipeName = BackgroundPipeServer.DefaultPipeName,
    TimeSpan? requestTimeout=null,Action<string,string>? diagnostic=null)
{
    // 保留已安装宿主调用的二参数构造签名，新增期限/诊断参数不破坏二进制兼容。
    public BackgroundPipeServer(IBackgroundControl control,string pipeName):this(control,pipeName,null,null){}
    // 用户确认将管道一并重命名；端点版本与分页、持久命令协议同时升级。
    public const string DefaultPipeName = "CRS.Worker.Service.v2";
    public const string ControlSuffix = ".control";
    private readonly object rateGate = new();
    private readonly Dictionary<string, (DateTimeOffset Started, int Count)> queries = [];
    private readonly Dictionary<string, (DateTimeOffset Started, int Count)> commands = [];
    private static readonly HashSet<string> Reads = ["GetHealth", "GetJobs", "GetHistory", "GetFilteredHistory", "GetExecution", "GetCommand", "QueryPlugin","GetEmailSettings","GetWebhookSettings"];
    private static readonly HashSet<string> Writes = ["RunNow", "Pause", "Resume", "ChangeSchedule", "CancelRun","SaveEmailSettings","TestEmailSettings","SaveWebhookSettings","TestWebhookSettings"];
    internal static bool IsMutation(string command) => Writes.Contains(command);
    public async Task RunAsync(CancellationToken token)
    {
        if(requestTimeout is {} timeout&&(timeout<TimeSpan.FromMilliseconds(100)||timeout>TimeSpan.FromSeconds(20)))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var loops = new List<Task>();
        // 第一个实例独占声明端点；后续实例使用相同 ACL，受程序数据目录租约保护。
        try
        {
            for (var i = 0; i < 4; i++) loops.Add(AcceptLoopAsync(false, CreatePipe(false, i == 0), stop.Token));
            for (var i = 0; i < 2; i++) loops.Add(AcceptLoopAsync(true, CreatePipe(true, i == 0), stop.Token));
            foreach (var loop in loops) _ = loop.ContinueWith(_ => stop.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAll(loops);
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(loops); } catch (Exception) { /* 保留原始监听异常并清理其它端点。 */ }
        }
    }
    private async Task AcceptLoopAsync(bool management, NamedPipeServerStream initial, CancellationToken token)
    {
        NamedPipeServerStream? pipe = initial;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(token);
                try { await HandleConnectionAsync(pipe, management, token); }
                catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or JsonException or UnauthorizedAccessException or ArgumentException)
                { /* 畸形请求、断连和超时仅影响当前连接。 */ }
                await pipe.DisposeAsync(); pipe = null;
                token.ThrowIfCancellationRequested(); pipe = CreatePipe(management, false);
            }
        }
        finally { if (pipe is not null) await pipe.DisposeAsync(); }
    }
    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, bool management, CancellationToken token)
    {
        using var reading=CancellationTokenSource.CreateLinkedTokenSource(token);reading.CancelAfter(TimeSpan.FromSeconds(5));
        var request = await Framing.ReadAsync<BackgroundRequest>(pipe, reading.Token);
        BackgroundCaller? caller = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException();
            if (identity.ImpersonationLevel < TokenImpersonationLevel.Impersonation || !identity.IsAuthenticated) throw new UnauthorizedAccessException();
            var principal = new WindowsPrincipal(identity);
            if (principal.IsInRole(new SecurityIdentifier(WellKnownSidType.NetworkSid, null))) throw new UnauthorizedAccessException();
            caller = new(identity.User!.Value, principal.IsInRole(WindowsBuiltInRole.Administrator));
        });
        if (caller is null) throw new UnauthorizedAccessException();
        var invalid = Limit(caller, management) ?? Validate(request, caller, management);
        var response = invalid is null ? await ExecuteRequestAsync(request,caller,token) : new BackgroundResponse(WorkerProtocol.Version, request.RequestId, invalid);
        if (invalid is not (null or "RateLimited" or "Busy") && Writes.Contains(request.Command)) await control.HandleAsync(request with { Command = "AuditDenied" }, caller, token);
        // 消息上限是最后防线；超限返回协议错误，不把服务伪装成断线。
        if (JsonSerializer.SerializeToUtf8Bytes(response).Length > 131072) response = new(WorkerProtocol.Version, request.RequestId, "PayloadTooLarge");
        using var replying=CancellationTokenSource.CreateLinkedTokenSource(token);replying.CancelAfter(TimeSpan.FromSeconds(3));
        await Framing.WriteAsync(pipe, response, replying.Token);
    }
    private async Task<BackgroundResponse> ExecuteRequestAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
    {
        using var processing=CancellationTokenSource.CreateLinkedTokenSource(token);
        processing.CancelAfter(requestTimeout??TimeSpan.FromSeconds(20));
        try
        {
            var response=await control.HandleAsync(request,caller,processing.Token);
            processing.Token.ThrowIfCancellationRequested();return response;
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested)
        {return Failure(IsMutation(request.Command)?"CommandOutcomeUnknown":"RequestTimedOut");}
        catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException)
        {return Failure(IsMutation(request.Command)?"CommandOutcomeUnknown":"RequestFailed");}
        BackgroundResponse Failure(string code)
        {
            try{diagnostic?.Invoke(request.Command,code);}catch(Exception){ /* 诊断失败不吞掉已确定的协议响应。 */ }
            return new(WorkerProtocol.Version,request.RequestId,code);
        }
    }
    private string? Validate(BackgroundRequest request, BackgroundCaller caller, bool management)
    {
        if (request.ProtocolVersion != WorkerProtocol.Version) return "ProtocolMismatch";
        if (request.RequestId == Guid.Empty || request.Command is null || request.Command.Length > 40) return "InvalidRequest";
        if (!Reads.Contains(request.Command) && !Writes.Contains(request.Command)) return "UnknownCommand";
        // 邮件设置单独有界；业务插件参数仍保持原4096字符上限。
        var payloadLimit=request.Command=="SaveEmailSettings"?8192:4096;
        if (request.PluginId?.Length > 80 || request.JobId?.Length > 80 || request.Cron?.Length > 120 || request.TimeZoneId?.Length > 100 || request.Cursor?.Length > 200 || request.Payload?.Length>payloadLimit || request.ExecutionId?.Length>32 || request.PageSize is < 1 or > 10) return "InvalidRequest";
        if (Writes.Contains(request.Command) && (!caller.CanManage || !management)) return "Forbidden";
        return null;
    }
    private string? Limit(BackgroundCaller caller, bool management)
    {
        lock (rateGate)
        {
            // 只读请求无需重放缓存；身份配额独立，修改请求在加密仓储中进行持久幂等校验。
            var bucket = management ? commands : queries; var now = DateTimeOffset.UtcNow;
            foreach (var sid in bucket.Where(x => now - x.Value.Started > TimeSpan.FromSeconds(10)).Select(x => x.Key).ToArray()) bucket.Remove(sid);
            if (!bucket.TryGetValue(caller.Sid, out var limit))
            {
                if (bucket.Count >= 256) return "Busy";
                limit = (now, 0);
            }
            if (limit.Count >= 64) return "RateLimited";
            bucket[caller.Sid] = (limit.Started, limit.Count + 1);
        }
        return null;
    }
    private NamedPipeServerStream CreatePipe(bool management, bool first)
    {
        var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent(); var owner = identity.User!; acl.SetOwner(owner);
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!management) acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName + (management ? ControlSuffix : ""), PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 16384, 16384, acl);
    }
}
