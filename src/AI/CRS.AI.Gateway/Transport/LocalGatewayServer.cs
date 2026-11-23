using System.IO.Pipes;
using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Gateway.Authorization;
using CRS.Worker.Ipc;

namespace CRS.AI.Gateway.Transport;

internal sealed class LocalGatewayServer(IAiReadGateway gateway, LocalAuthorizations authorizations, string pipeName)
{
    public async Task RunAsync(CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var loops = new List<Task>();
        try
        {
            Start(management: false, count: 4);
            Start(management: true, count: 2);
            foreach (var loop in loops)
                _ = loop.ContinueWith(_ => stop.Cancel(), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAll(loops);
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(loops); }
            catch (Exception) { /* 保留最初的监听异常。 */ }
        }

        void Start(bool management, int count)
        {
            for (var i = 0; i < count; i++)
                loops.Add(AcceptAsync(CreatePipe(management, first: i == 0), management, stop.Token));
        }
    }

    private async Task AcceptAsync(NamedPipeServerStream listener, bool management, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(token);
                try
                {
                    if (management) await HandleAccessAsync(listener, token);
                    else await HandleReadAsync(listener, token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                              JsonException or InvalidDataException or OperationCanceledException)
                {
                    // 畸形请求、身份失败和断连只结束当前连接。
                }

                token.ThrowIfCancellationRequested();
                // 显式 Disconnect 会丢弃客户端尚未读取的响应。先补上监听实例，再关闭本次服务端句柄。
                var previous = listener;
                listener = CreatePipe(management, first: false);
                await previous.DisposeAsync();
            }
        }
        finally { await listener.DisposeAsync(); }
    }

    private async Task HandleAccessAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var request = await ReadAsync<LocalAccessRequest>(pipe, token);
        _ = LocalPipeSecurity.Authenticate(pipe, management: true);
        var code = "InvalidRequest";
        if (request.Version == LocalGatewayProtocol.Version && request.RequestId != Guid.Empty)
        {
            code = request.Action switch
            {
                "Status" when request.TargetSid is null => "Ok",
                "Grant" => await authorizations.SetAsync(request.TargetSid, allow: true, token),
                "Revoke" => await authorizations.SetAsync(request.TargetSid, allow: false, token),
                _ => "InvalidRequest"
            };
        }
        await ReplyAsync(pipe, new LocalAccessResponse(LocalGatewayProtocol.Version, request.RequestId, code), token);
    }

    private async Task HandleReadAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var request = await ReadAsync<LocalReadRequest>(pipe, token);
        var sid = LocalPipeSecurity.Authenticate(pipe, management: false);
        if (request.Version != LocalGatewayProtocol.Version || request.RequestId == Guid.Empty ||
            string.IsNullOrEmpty(request.Tool) || request.Tool.Length > 128 ||
            request.Arguments.ValueKind == JsonValueKind.Undefined)
        {
            await SendAsync(AiResult.Reject("unlisted", "InvalidArguments"), token);
            return;
        }

        using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watch = WatchDisconnectAsync(pipe, disconnected, watching.Token);
        try
        {
            using var lease = await authorizations.AcquireAsync(sid, disconnected.Token);
            if (lease is null)
            {
                var tool = request.Tool is AiCatalog.Overview or AiCatalog.Coverage ? request.Tool : "unlisted";
                await SendAsync(AiResult.Reject(tool, "Unauthorized"), disconnected.Token);
                return;
            }

            var result = await gateway.ExecuteAsync(new AiCaller(sid, true),
                new AiCall(request.Tool, request.Arguments), lease.Token);
            await authorizations.ReplyAsync(sid, lease, current =>
                SendAsync(current ? result : AiResult.Reject(result.Tool, "Unauthorized"), disconnected.Token),
                disconnected.Token);
        }
        finally
        {
            watching.Cancel();
            await watch;
        }

        Task SendAsync(AiResult result, CancellationToken cancellation) =>
            ReplyAsync(pipe, new LocalReadResponse(LocalGatewayProtocol.Version, request.RequestId, result), cancellation);
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource disconnected, CancellationToken token)
    {
        try
        {
            // 每个连接只接受一条请求。EOF 或多余数据都取消仍在进行的查询。
            await pipe.ReadExactlyAsync(new byte[1], token);
            disconnected.Cancel();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { disconnected.Cancel(); }
    }

    private static async Task<T> ReadAsync<T>(Stream pipe, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        return await Framing.ReadAsync<T>(pipe, deadline.Token, LocalGatewayProtocol.MaxRequestBytes);
    }

    private static async Task ReplyAsync<T>(Stream pipe, T reply, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await Framing.WriteAsync(pipe, reply, deadline.Token);
    }

    private NamedPipeServerStream CreatePipe(bool management, bool first) =>
        NamedPipeServerStreamAcl.Create(pipeName + (management ? LocalGatewayProtocol.ControlSuffix : ""),
            PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            16384, 8192, LocalPipeSecurity.CreateAcl(management));
}
