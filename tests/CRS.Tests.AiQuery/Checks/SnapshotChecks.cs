using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Query;
using CRS.Tests.AiQuery.Fixtures;

namespace CRS.Tests.AiQuery.Checks;

internal static class SnapshotChecks
{
    private const string FirstRow = """
        {"Market":"SSE","Ticker":"510300","Name":"示例","AsOf":null,"Shares":null,"Change1":null,"Change5":null,"Change20":null,"Status":"Unknown","CoveredDays":0}
        """;

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var arguments = JsonDocument.Parse("{}");
        var call = new AiCall(AiCatalog.Overview, arguments.RootElement);
        var caller = new AiCaller("snapshot-check", true);
        var worker = new FakeWorker();
        var gateway = AiReadGatewayFactory.Create(worker, new FakeAuditSink());
        var firstPage = $"[{FirstRow}]";
        var secondRow = FirstRow.Replace("510300", "510050", StringComparison.Ordinal);
        var secondPage = $"[{secondRow}]";

        // 分页读取与一次读取都应产生同样的公开结果指纹。
        worker.Handler = (_, payload, _) =>
        {
            using var request = JsonDocument.Parse(payload);
            var root = request.RootElement;
            if (root.GetProperty("Offset").GetInt32() == 0)
                return Task.FromResult(FakeWorker.Page(firstPage, next: 1, total: 2));
            check(root.GetProperty("Snapshot").GetString() == "fixed-poc-snapshot",
                "后续页沿用 Worker 快照令牌");
            return Task.FromResult(FakeWorker.Page(secondPage, total: 2));
        };
        var paged = await gateway.ExecuteAsync(caller, call, default);
        worker.Handler = (_, _, _) => Task.FromResult(FakeWorker.Page(
            $"[{FirstRow},{secondRow}]", total: 2, snapshot: "new-worker-snapshot"));
        var single = await gateway.ExecuteAsync(caller, call, default);
        check(paged.Status == "Partial" && paged.Data?.GetArrayLength() == 2 &&
              paged.DataVersion is { Length: 64 } && paged.DataVersion == single.DataVersion,
            "分页大小与临时令牌不改变相同公开数据的版本");

        worker.Handler = (_, _, _) => Task.FromResult(FakeWorker.Page(firstPage, total: 1));
        var changed = await gateway.ExecuteAsync(caller, call, default);
        check(changed.DataVersion is { Length: 64 } && changed.DataVersion != paged.DataVersion,
            "公开结果发生变化时数据版本变化");

        foreach (var lastPage in new[]
        {
            FakeWorker.Page("[]"),
            FakeWorker.Page(secondPage, total: 3),
            FakeWorker.Page(secondPage, total: 2, snapshot: "different-snapshot")
        })
        {
            var pages = new Queue<WorkerReadReply>([FakeWorker.Page(firstPage, next: 1, total: 2), lastPage]);
            worker.Handler = (_, _, _) => Task.FromResult(pages.Dequeue());
            var rejected = await gateway.ExecuteAsync(caller, call, default);
            check(rejected.Code == "UpstreamInvalid" && rejected.Data is null && rejected.DataVersion is null,
                "拒绝提前结束、总行数变化和快照混页");
        }
    }
}
