using CRS.Worker.Ipc;
namespace CRS.AI.Query;

// Narrow and fakeable IPC boundary; never a database or a task-management API.
public interface IWorkerPublicReadClient
{
    Task<WorkerReadReply> QueryAsync(string operation, string payload, CancellationToken token);
}
public sealed record WorkerReadReply(string Code, string? Payload);

public sealed class WorkerPublicReadClient(BackgroundPipeClient client) : IWorkerPublicReadClient
{
    public async Task<WorkerReadReply> QueryAsync(string operation, string payload, CancellationToken token)
    {
        if (operation is not ("overview" or "coverage"))
            throw new ArgumentOutOfRangeException(nameof(operation));
        var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
            "QueryPlugin", PluginId: "crs.national-team", Payload: payload), token);
        return new(response.Code, response.Payload);
    }
}
