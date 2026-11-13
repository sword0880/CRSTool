using System.ComponentModel;
using System.Text.Json;
using CRS.AI.Contracts;

namespace CRS.AI.Gateway.Transport;

/// <summary>用当前 Windows 进程身份连接已安装的只读 Gateway。</summary>
public sealed class WindowsAiReadClient
{
    private readonly LocalGatewayClient client;

    /// <summary>使用正式管道，并在发送请求前核对管道服务端进程。</summary>
    public WindowsAiReadClient() : this(new(LocalGatewayProtocol.PipeName, verifyService: true)) { }

    // 隔离验收可注入临时管道客户端。
    internal WindowsAiReadClient(LocalGatewayClient client) => this.client = client;

    /// <summary>执行有时限的公开 ETF 查询，并将管道故障转换为固定结果码。</summary>
    public async Task<AiResult> ExecuteAsync(AiCall call, CancellationToken token)
    {
        if (call.Tool is not (AiCatalog.Overview or AiCatalog.Coverage))
            return AiResult.Reject("unlisted", "UnknownTool");
        if (call.Arguments.ValueKind != JsonValueKind.Object || call.Arguments.GetRawText().Length > 4096)
            return AiResult.Reject(call.Tool, "InvalidArguments");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var reply = await client.CallAsync(new(LocalGatewayProtocol.Version, Guid.NewGuid(),
                call.Tool, call.Arguments), timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return reply.Result;
        }
        catch (UnauthorizedAccessException) { return AiResult.Reject(call.Tool, "Unauthorized"); }
        // 管道超时视为 Gateway 不可用，调用方主动取消则继续向上层传播。
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return AiResult.Reject(call.Tool, "GatewayUnavailable"); }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or Win32Exception)
        { return AiResult.Reject(call.Tool, "GatewayUnavailable"); }
    }
}
