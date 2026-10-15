using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
namespace CRS.Background.Ipc;

/// <summary>显式管道 ACL，拒绝网络登录 SID；管理权限来自模拟后的本机 Windows 令牌。</summary>
public sealed class BackgroundPipeServer(IBackgroundControl control, string pipeName = BackgroundPipeServer.DefaultPipeName)
{
    public const string DefaultPipeName = "CRS.Background.Service.v1";
    private readonly Dictionary<Guid, DateTimeOffset> seen = [];
    private static readonly HashSet<string> ReadCommands = ["GetHealth", "GetJobs", "GetHistory"];
    private static readonly HashSet<string> WriteCommands = ["RunNow", "Pause", "Resume", "ChangeSchedule", "CancelRun"];

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            await pipe.WaitForConnectionAsync(token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var request = await Framing.ReadAsync<BackgroundRequest>(pipe, deadline.Token);
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
                var invalid = Validate(request, caller);
                var response = invalid is null
                    ? await control.HandleAsync(request, caller, deadline.Token)
                    : new BackgroundResponse(Framing.Version, request.RequestId, invalid);
                // 拒绝的管理操作也交给控制器审计，但绝不执行任何业务命令。
                if (invalid is not null && WriteCommands.Contains(request.Command))
                    await control.HandleAsync(request with { Command = "AuditDenied" }, caller, deadline.Token);
                await Framing.WriteAsync(pipe, response, deadline.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
            { /* 单个客户端断连、超时或畸形输入不终止无人值守服务。 */ }
        }
    }
    private string? Validate(BackgroundRequest request, BackgroundCaller caller)
    {
        if (request.ProtocolVersion != Framing.Version) return "ProtocolMismatch";
        if (request.RequestId == Guid.Empty || request.Command is null || request.Command.Length > 40) return "InvalidRequest";
        if (!ReadCommands.Contains(request.Command) && !WriteCommands.Contains(request.Command)) return "UnknownCommand";
        if (request.PluginId?.Length > 80 || request.JobId?.Length > 80 || request.Cron?.Length > 120 || request.TimeZoneId?.Length > 100) return "InvalidRequest";
        if (WriteCommands.Contains(request.Command) && !caller.CanManage) return "Forbidden";
        var now = DateTimeOffset.UtcNow;
        foreach (var id in seen.Where(x => now - x.Value > TimeSpan.FromMinutes(10)).Select(x => x.Key).ToArray()) seen.Remove(id);
        if (seen.ContainsKey(request.RequestId)) return "ReplayRejected";
        if (seen.Count >= 2048) return "Busy";
        seen.Add(request.RequestId, now);
        return null;
    }
    private NamedPipeServerStream CreatePipe()
    {
        var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
        var owner = WindowsIdentity.GetCurrent().User!;
        acl.SetOwner(owner);
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        // 排除 CreateNewInstance，普通用户只能访问已有服务管道，不能伪造第二个服务端。
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 16384, 16384, acl);
    }
}
