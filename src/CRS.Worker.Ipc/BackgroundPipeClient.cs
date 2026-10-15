using System.IO.Pipes;
using System.Security.Principal;
namespace CRS.Worker.Ipc;

/// <summary>桌面只通过本机管道访问服务；服务不可用时明确返回，不启动替代调度器。</summary>
public sealed class BackgroundPipeClient(string pipeName = BackgroundPipeServer.DefaultPipeName)
{
    public async Task<BackgroundResponse> SendAsync(BackgroundRequest request, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var identity = WindowsIdentity.GetCurrent();
        var management = BackgroundPipeServer.IsMutation(request.Command) && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        await using var pipe = new NamedPipeClientStream(".", pipeName + (management ? BackgroundPipeServer.ControlSuffix : ""), PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(deadline.Token);
        if (pipeName == BackgroundPipeServer.DefaultPipeName) PipeServerIdentity.Verify(pipe);
        await Framing.WriteAsync(pipe, request, deadline.Token);
        var response = await Framing.ReadAsync<BackgroundResponse>(pipe, deadline.Token);
        if (response.RequestId != request.RequestId || response.ProtocolVersion != Framing.Version)
            throw new InvalidDataException("服务协议或请求标识不匹配。");
        return response;
    }
    public Task<BackgroundResponse> QueryAsync(string command, CancellationToken token = default) =>
        SendAsync(new(Framing.Version, Guid.NewGuid(), command), token);
}
