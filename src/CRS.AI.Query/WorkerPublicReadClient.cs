using System.Text.Json;
using CRS.Worker.Ipc;
namespace CRS.AI.Query;

// Narrow and fakeable IPC boundary; never a database or task-management API.
public interface IWorkerPublicReadClient
{
    Task<WorkerReadReply> QueryAsync(string operation, string payload, CancellationToken token);
}
public sealed record WorkerReadReply(string Code, string? Payload);

public sealed class WorkerPublicReadClient(BackgroundPipeClient client) : IWorkerPublicReadClient
{
    public async Task<WorkerReadReply> QueryAsync(string operation, string payload, CancellationToken token)
    {
        ValidateReadOnlyPayload(operation, payload);
        var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
            "QueryPlugin", PluginId: "crs.national-team", Payload: payload), token);
        return new(response.Code, response.Payload);
    }

    /// <summary>
    /// Second in-process guard: even another caller of this concrete bridge cannot
    /// smuggle release-query/export/research-submit via a matching safe operation argument.
    /// Actual external authorization and a restricted Worker service SID remain separate P0 gates.
    /// </summary>
    public static void ValidateReadOnlyPayload(string operation, string payload)
    {
        if (operation is not ("overview" or "coverage") || string.IsNullOrEmpty(payload) ||
            payload.Length > 4096) throw new InvalidDataException("Unsupported public read operation.");
        using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid read request.");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Operation", "Market", "Ticker", "From", "To", "Offset", "Count", "Snapshot", "ContractVersion"
        };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name) || !names.Add(property.Name))
                throw new InvalidDataException("Unexpected read request property.");
        if (!root.TryGetProperty("Operation", out var command) ||
            command.ValueKind != JsonValueKind.String || command.GetString() != operation ||
            !root.TryGetProperty("ContractVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
            !root.TryGetProperty("Count", out var count) || count.ValueKind != JsonValueKind.Number ||
            count.GetInt32() is < 1 or > 64 ||
            !root.TryGetProperty("Offset", out var offset) || offset.ValueKind != JsonValueKind.Number ||
            offset.GetInt32() is < 0 or > 64)
            throw new InvalidDataException("Invalid read request values.");
        if (root.TryGetProperty("Snapshot", out var snapshot) &&
            snapshot.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException("Invalid snapshot token.");
        if (root.TryGetProperty("Snapshot", out snapshot) &&
            snapshot.ValueKind == JsonValueKind.String && snapshot.GetString()!.Length > 128)
            throw new InvalidDataException("Invalid snapshot length.");
    }
}
