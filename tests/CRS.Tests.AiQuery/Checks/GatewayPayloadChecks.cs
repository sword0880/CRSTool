using CRS.Worker.Ipc;

namespace CRS.Tests.AiQuery.Checks;

internal static class GatewayPayloadChecks
{
    private const string Overview = """{"Operation":"overview","Offset":0,"Count":10,"ContractVersion":1}""";
    private const string Coverage = """
        {"Operation":"coverage","Market":"SSE","Ticker":"510300","From":"2026-11-02","To":"2026-11-06","Offset":0,"Count":10,"ContractVersion":1}
        """;

    public static void Run(Action<bool, string> check)
    {
        check(AiGatewayWorkerRequestPolicy.MatchesPayload("overview", Overview) &&
              AiGatewayWorkerRequestPolicy.MatchesPayload("coverage", Coverage), "Gateway 允许合法的公开 ETF 首次查询");
        var continuation = Overview.Replace("\"Offset\":0", "\"Offset\":60,\"Snapshot\":\"issued-snapshot\"")
            .Replace("\"Count\":10", "\"Count\":4");
        check(AiGatewayWorkerRequestPolicy.MatchesPayload("overview", continuation), "Gateway 允许有界快照续页");

        // 这些载荷直接面对 Worker。即使上游未调用参数检查，也不能扩大查询范围。
        foreach (var (operation, payload, reason) in new[]
        {
            ("overview", Overview.Replace("\"Offset\":0", "\"Offset\":1"), "续页缺少快照"),
            ("overview", Overview.Replace("\"Offset\":0", "\"Offset\":0,\"Snapshot\":\"foreign\""), "首页夹带快照"),
            ("overview", continuation.Replace("\"Count\":4", "\"Count\":5"), "续页跨过行数上限"),
            ("overview", Overview.Replace("\"Count\":10", "\"Count\":10,\"Count\":1"), "重复字段"),
            ("overview", Overview.Replace("\"Count\":10", "\"Count\":2147483648"), "整数溢出"),
            ("overview", Overview.Replace("\"Count\":10", "\"Count\":10,\"Ticker\":\"510300\""), "概览夹带证券范围"),
            ("overview", Overview.Replace("overview", "\\uD800"), "无效 Unicode"),
            ("coverage", Coverage.Replace("510300", "../100"), "无效证券代码"),
            ("coverage", Coverage.Replace("2026-11-06", "2026-12-03"), "超出覆盖日期范围"),
            ("coverage", Coverage.Replace("2026-11-06", "2026-11-01"), "日期倒置")
        })
            check(!AiGatewayWorkerRequestPolicy.MatchesPayload(operation, payload), "Gateway 拒绝" + reason);
    }
}
