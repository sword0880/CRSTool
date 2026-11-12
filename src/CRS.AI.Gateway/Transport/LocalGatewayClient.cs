using System.IO.Pipes;
using System.Security.Principal;
using CRS.Worker.Ipc;

namespace CRS.AI.Gateway.Transport;

internal sealed class LocalGatewayClient(string pipeName, bool verifyService)
{
    public async Task<LocalReadResponse> CallAsync(LocalReadRequest request, CancellationToken token)
    {
        var reply = await SendAsync<LocalReadRequest, LocalReadResponse>(request, management: false, token);
        VerifyReply(reply.Version, reply.RequestId, request.RequestId);
        return reply;
    }

    public async Task<LocalAccessResponse> SetAccessAsync(LocalAccessRequest request, CancellationToken token)
    {
        var reply = await SendAsync<LocalAccessRequest, LocalAccessResponse>(request, management: true, token);
        VerifyReply(reply.Version, reply.RequestId, request.RequestId);
        return reply;
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(TRequest request, bool management, CancellationToken token)
    {
        // 仅提供身份识别权限。服务端核对进程之前不发送请求。
        await using var pipe = new NamedPipeClientStream(".", pipeName + (management ? LocalGatewayProtocol.ControlSuffix : ""),
            LocalGatewayProtocol.ClientRights, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(connecting.Token);
        }
        if (verifyService) PipeServerIdentity.Verify(pipe, AiGatewayWorkerEndpoint.ServiceName);
        await Framing.WriteAsync(pipe, request, token);
        return await Framing.ReadAsync<TResponse>(pipe, token);
    }

    private static void VerifyReply(int version, Guid actual, Guid expected)
    {
        if (version != LocalGatewayProtocol.Version || actual != expected)
            throw new InvalidDataException("Gateway 响应版本或请求标识不匹配。");
    }
}
