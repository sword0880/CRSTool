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
    private readonly Dictionary<string, (DateTimeOffset Started, int Count)> uploads = [];
    private readonly Dictionary<string, (DateTimeOffset Started, int Count)> gatewayQueries = [];
    private static readonly HashSet<string> Reads = ["GetHealth", "GetJobs", "GetDiagnosticJobs", "GetHistoryJobs", "GetHistory", "GetFilteredHistory", "GetHistoryByDate", "GetTaskActivity", "GetExecution", "GetCommand", "QueryPlugin","GetEmailSettings","GetWebhookSettings","BeginDataCleanupPreview","GetDataCleanupStatus","GetDataCleanupTasks",CRS.Maintenance.DataCleanupTask.HistoryCommand,WorkerProtocol.GetAnnualExchangeRates,WorkerProtocol.GetWorkerLogSettings];
    private static readonly HashSet<string> Writes = ["RunNow", "Pause", "Resume", "ChangeSchedule", "CancelRun","SaveEmailSettings","TestEmailSettings","SaveWebhookSettings","TestWebhookSettings","ExecuteDataCleanup","PutPluginDocument","PauseAutoCleanup","SubmitDataCleanupTask","AcknowledgeDataCleanupTask",WorkerProtocol.SaveWorkerLogSettings];
    internal static bool IsMutation(string command) => Writes.Contains(command);
    public Task RunAsync(CancellationToken token) =>
        RunAsync(token, enableAiGateway: string.Equals(pipeName, DefaultPipeName, StringComparison.OrdinalIgnoreCase));

    internal async Task RunAsync(CancellationToken token, bool enableAiGateway)
    {
        if(requestTimeout is {} timeout&&(timeout<TimeSpan.FromMilliseconds(100)||timeout>TimeSpan.FromSeconds(20)))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var loops = new List<Task>();
        // 第一个实例独占声明端点；后续实例使用相同 ACL，受程序数据目录租约保护。
        try
        {
            StartChannel(Channel.DesktopRead, 4);
            StartChannel(Channel.Management, 2);
            if (enableAiGateway) StartChannel(Channel.AiPublicRead, 2);
            foreach (var loop in loops) _ = loop.ContinueWith(_ => stop.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAll(loops);
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(loops); } catch (Exception) { /* 保留原始监听异常并清理其它端点。 */ }
        }

        void StartChannel(Channel channel, int count)
        {
            for (var i = 0; i < count; i++)
                loops.Add(AcceptLoopAsync(channel, CreatePipe(channel, i == 0), stop.Token));
        }
    }
    private async Task AcceptLoopAsync(Channel channel, NamedPipeServerStream initial, CancellationToken token)
    {
        NamedPipeServerStream? pipe = initial;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(token);
                try { await HandleConnectionAsync(pipe, channel, token); }
                catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or JsonException or UnauthorizedAccessException or ArgumentException)
                { /* 畸形请求、断连和超时仅影响当前连接。 */ }
                await pipe.DisposeAsync(); pipe = null;
                token.ThrowIfCancellationRequested(); pipe = CreatePipe(channel, false);
            }
        }
        finally { if (pipe is not null) await pipe.DisposeAsync(); }
    }
    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, Channel channel, CancellationToken token)
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
            var user = identity.User ?? throw new UnauthorizedAccessException();
            var gatewayAccount = user.Equals(AiGatewayWorkerEndpoint.Identity);
            // 仅接受以专用虚拟账户登录的服务进程。共享账户持有同名服务组也不能代用。
            if (channel == Channel.AiPublicRead)
            {
                if (!gatewayAccount) throw new UnauthorizedAccessException();
                caller = new(user.Value, CanManage: false);
                return;
            }
            if (gatewayAccount || identity.Groups?.Contains(AiGatewayWorkerEndpoint.Identity) == true)
                throw new UnauthorizedAccessException();
            caller = new(user.Value, principal.IsInRole(WindowsBuiltInRole.Administrator));
        });
        if (caller is null) throw new UnauthorizedAccessException();
        var management = channel == Channel.Management;
        var gateway = channel == Channel.AiPublicRead;
        var invalid = Limit(caller, channel, request.Command == "PutPluginDocument");
        if (invalid is null)
        {
            // Gateway 请求先按专用白名单判定，拒绝请求不会触发业务控制层或持久命令审计。
            invalid = gateway && !AiGatewayWorkerRequestPolicy.Allows(request)
                ? "Forbidden" : Validate(request, caller, management);
        }
        var response = invalid is null ? request.Command=="QueryPlugin"?
            await ExecuteConnectedQueryAsync(pipe,request,caller,token):await ExecuteRequestAsync(request,caller,token)
            : new BackgroundResponse(WorkerProtocol.Version, request.RequestId, invalid);
        if (!gateway && invalid is not (null or "RateLimited" or "Busy") && Writes.Contains(request.Command))
            await control.HandleAsync(request with { Command = "AuditDenied" }, caller, token);
        // 消息上限是最后防线；超限返回协议错误，不把服务伪装成断线。
        if (JsonSerializer.SerializeToUtf8Bytes(response).Length > 131072) response = new(WorkerProtocol.Version, request.RequestId, "PayloadTooLarge");
        using var replying=CancellationTokenSource.CreateLinkedTokenSource(token);replying.CancelAfter(TimeSpan.FromSeconds(3));
        await Framing.WriteAsync(pipe, response, replying.Token);
    }
    private async Task<BackgroundResponse> ExecuteConnectedQueryAsync(NamedPipeServerStream pipe,BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
    {
        using var disconnected=CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watching=CancellationTokenSource.CreateLinkedTokenSource(token);
        var watch=WatchAsync();
        try{return await ExecuteRequestAsync(request,caller,disconnected.Token);}
        finally{watching.Cancel();await watch;}
        async Task WatchAsync()
        {
            try
            {
                // 一个连接只有一条请求；读到EOF或多余数据时，停止已无人等待的只读查询。
                await pipe.ReadExactlyAsync(new byte[1],watching.Token);
                disconnected.Cancel();
            }
            catch(OperationCanceledException) when(watching.IsCancellationRequested){}
            catch(IOException){disconnected.Cancel();}
        }
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
        if (!Reads.Contains(request.Command) && !Writes.Contains(request.Command)&&request.Command is not ("GetPluginDocument" or "ListPluginDocuments")) return "UnknownCommand";
        // 邮件设置单独有界；业务插件参数仍保持原4096字符上限。
        var payloadLimit=request.Command=="PutPluginDocument"?32768:request.Command=="SaveEmailSettings"?8192:4096;
        if (request.PluginId?.Length > 80 || request.JobId?.Length > 80 || request.Cron?.Length > 120 || request.TimeZoneId?.Length > 100 || request.Cursor?.Length > 200 || request.Payload?.Length>payloadLimit || request.ExecutionId?.Length>32 || request.PageSize < 1 || request.PageSize > (request.Command=="GetHistoryByDate"?50:10)) return "InvalidRequest";
        if (Writes.Contains(request.Command) && (!caller.CanManage || !management)) return "Forbidden";
        return null;
    }
    private string? Limit(BackgroundCaller caller, Channel channel, bool upload)
    {
        lock (rateGate)
        {
            // 只读请求无需重放缓存；身份配额独立，修改请求在加密仓储中进行持久幂等校验。
            var bucket = channel switch
            {
                Channel.AiPublicRead => gatewayQueries,
                _ when upload => uploads,
                Channel.Management => commands,
                _ => queries
            };
            var now = CRS.Timing.BeijingClock.Now;
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
    private NamedPipeServerStream CreatePipe(Channel channel, bool first)
    {
        var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent(); var owner = identity.User!; acl.SetOwner(owner);
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (channel == Channel.AiPublicRead)
        {
            acl.AddAccessRule(new(AiGatewayWorkerEndpoint.Identity, AiGatewayWorkerEndpoint.ClientRights, AccessControlType.Allow));
        }
        else
        {
            acl.AddAccessRule(new(AiGatewayWorkerEndpoint.Identity, PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var kind in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                acl.AddAccessRule(new(new SecurityIdentifier(kind, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            if (channel == Channel.DesktopRead)
                acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        }
        var suffix = channel switch
        {
            Channel.Management => ControlSuffix,
            Channel.AiPublicRead => AiGatewayWorkerEndpoint.PipeSuffix,
            _ => ""
        };
        return NamedPipeServerStreamAcl.Create(pipeName + suffix, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 16384, 16384, acl);
    }

    private enum Channel { DesktopRead, Management, AiPublicRead }
}
