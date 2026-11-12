using CRS.Worker.Ipc;
namespace CRS.AI.Query;

// 此边界只调用固定的公开 ETF 查询。
public interface IWorkerPublicReadClient
{
    bool UsesSinglePage => false;
    Task<WorkerReadReply> QueryAsync(string operation, string payload, CancellationToken token);
}
public sealed record WorkerReadReply(string Code, string? Payload);

public sealed class WorkerPublicReadClient(BackgroundPipeClient client) : IWorkerPublicReadClient
{
    public bool UsesSinglePage { get; private init; }

    /// <summary>服务宿主使用专用端点。显式传入客户端的构造方式用于桌面诊断和隔离测试。</summary>
    public static WorkerPublicReadClient ForGateway() => new(BackgroundPipeClient.ForAiGateway()) { UsesSinglePage = true };

    public async Task<WorkerReadReply> QueryAsync(string operation, string payload, CancellationToken token)
    {
        ValidateReadOnlyPayload(operation, payload);
        var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
            "QueryPlugin", PluginId: AiGatewayWorkerRequestPolicy.PluginId, Payload: payload), token);
        return new(response.Code, response.Payload);
    }

    /// <summary>
    /// 核对外层操作与载荷中的业务操作，禁止借公开查询调用释放、导出或研究提交。
    /// </summary>
    public static void ValidateReadOnlyPayload(string operation, string payload)
    {
        if (!AiGatewayWorkerRequestPolicy.MatchesPayload(operation, payload))
            throw new InvalidDataException("公开 ETF 查询载荷不符合允许范围。");
    }
}
