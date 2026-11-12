using System.Text.Json;
using CRS.AI.Query;

namespace CRS.Tests.AiQuery.Fixtures;

internal sealed class FakeWorker : IWorkerPublicReadClient
{
    public bool UsesSinglePage { get; init; }
    public int Calls { get; private set; }
    public List<string> Operations { get; } = [];
    public List<string> Payloads { get; } = [];
    public Func<string,string,CancellationToken,Task<WorkerReadReply>> Handler { get; set; } =
        (_,_,_) => Task.FromResult(Page("[]"));
    public Task<WorkerReadReply> QueryAsync(string operation, string payload, CancellationToken token)
    {
        Calls++; Operations.Add(operation); Payloads.Add(payload);
        return Handler(operation,payload,token);
    }
    public static WorkerReadReply Page(string items, string? metadata = null, int? next = null,
        int? total = null, string snapshot = "fixed-poc-snapshot", bool? singlePage = null) =>
        new("Ok", JsonSerializer.Serialize(new { Items = items, Snapshot = snapshot,
            NextOffset = next, Metadata = metadata, ContractVersion = 1, TotalRows = total, SinglePage = singlePage }));
}
