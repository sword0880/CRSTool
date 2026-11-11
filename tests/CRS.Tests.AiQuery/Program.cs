using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Query;
using CRS.AI.Adapters;
using CRS.Worker.Ipc;

var passed = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine("PASS AI读POC: " + name);
}
static JsonElement Args(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}
static AiCall Overview(string arg = "{}") => new(AiCatalog.Overview, Args(arg));
static AiCall Coverage(string arg) => new(AiCatalog.Coverage, Args(arg));
const string coverageArgs = """{"market":"SSE","ticker":"510300","from":"2026-11-01","to":"2026-11-03"}""";

// The concrete IPC bridge must reject a forged read operation even if another caller bypasses the facade.
void RejectWire(string operation, string payload, string name)
{
    try { WorkerPublicReadClient.ValidateReadOnlyPayload(operation, payload); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new InvalidOperationException(name);
}
RejectWire("overview", """{"Operation":"release-query","Count":10,"Offset":0,"ContractVersion":1}""",
    "IPC 二次校验拒绝伪装成 overview 的状态操作");
RejectWire("overview", """{"Operation":"overview","Count":10,"Offset":0,"ContractVersion":1,"RefreshSource":true}""",
    "IPC 二次校验拒绝未授权业务字段");
RejectWire("coverage", """{"Operation":"coverage","Count":10000,"Offset":0,"ContractVersion":1}""",
    "IPC 二次校验拒绝无界读取");

var fake = new FakeWorker();
var gateway = new AiReadQueryService(fake);
var caller = new AiCaller("trusted-local-test", true);
var mcp = new McpReadToolAdapter(gateway);
var openai = new OpenAiFunctionReadAdapter(gateway);
Check(mcp.ListTools().Count == 2 && openai.ListFunctions().Count == 2 &&
      mcp.ListTools().Select(x => x.Name).SequenceEqual(openai.ListFunctions().Select(x => x.Name)) &&
      openai.ListFunctions().All(x => x.Type == "function" && x.Strict),
      "MCP 与 OpenAI 工具名/Strict 契约源相同");

var denied = await mcp.CallToolAsync(new AiCaller("anonymous", false), Overview(), default);
Check(denied.Code == "Unauthorized" && fake.Calls == 0, "未经授权时不访问 Worker");
Check((await gateway.ExecuteAsync(caller, new AiCall("arbitrary-sql", Args("{}")), default)).Code == "UnknownTool"
      && fake.Calls == 0, "任意工具/SQL 指令不可透传");
foreach (var invalid in new[]
{
    """{"unexpected":true}""", """{"market":"SSE"}""", """{"market":"SSE","market":"SZSE"}"""
})
    Check((await gateway.ExecuteAsync(caller, Overview(invalid), default)).Code == "InvalidArguments"
          && fake.Calls == 0, "拒绝 overview 非空/重复参数");
foreach (var invalid in new[]
{
    """{"market":"NYSE","ticker":"510300","from":"2026-11-01","to":"2026-11-03"}""",
    """{"market":"SSE","ticker":"510300","from":"2026-01-01","to":"2026-11-03"}""",
    """{"market":"SSE","ticker":"../../xx","from":"2026-11-01","to":"2026-11-03"}"""
})
    Check((await gateway.ExecuteAsync(caller, Coverage(invalid), default)).Code == "InvalidArguments"
          && fake.Calls == 0, "拒绝非法市场、时间窗和证券标识");

fake.Handler = (operation, payload, token) =>
{
    Check(operation == "overview", "IPC 封装只允许固定 overview 操作");
    using var req = JsonDocument.Parse(payload);
    Check(req.RootElement.GetProperty("Operation").GetString() == "overview", "Worker业务参数不是模型原文");
    var rows = new[]
    {
        new {Market="SSE",Ticker="510300",Name="sample",AsOf=(DateOnly?)null,
            Shares=(decimal?)null,Change1=(decimal?)null,Change5=(decimal?)null,Change20=(decimal?)null,
            Status="Unknown",CoveredDays=0, AccountNumber="SECRET_MUST_NOT_PASS"}
    };
    return Task.FromResult(FakeWorker.Page(JsonSerializer.Serialize(rows)));
};
var a = await mcp.CallToolAsync(caller, Overview(), default);
var b = await openai.ExecuteFunctionAsync(caller, Overview(), default);
Check(a.Status == "Partial" && b.Status == "Partial" &&
      a.Data?.GetRawText() == b.Data?.GetRawText(), "MCP/OpenAI结果共用相同事实投影");
var item = a.Data!.Value[0];
Check(item.GetProperty("Shares").ValueKind == JsonValueKind.Null &&
      !item.TryGetProperty("AccountNumber", out _) && !a.Data.Value.GetRawText().Contains("SECRET"),
      "未知份额仍为 null、未许可字段被剔除");

fake.Handler = (operation, payload, token) =>
{
    Check(operation == "coverage", "IPC 封装只允许固定 coverage 操作");
    var metadata = JsonSerializer.Serialize(new
    {
        Market="SSE",Ticker="510300",From=new DateOnly(2026,11,1),To=new DateOnly(2026,11,3),
        RegisteredTradingDays=2,AcceptedDays=1,MissingDays=1,PendingDays=0,
        CriticalDays=0,RejectedDays=0,CoveragePercent=(decimal?)50m,
        CalendarVerified=false,CalendarRangeCovered=false,State="CalendarRangeIncomplete",
        InternalPath="NEVER_EXPOSE"
    });
    var days = JsonSerializer.Serialize(new[]
    {
        new {Date=new DateOnly(2026,11,2),State="Accepted",LastAttempt="PRIVATE"},
        new {Date=new DateOnly(2026,11,3),State="Missing",LastAttempt="PRIVATE"}
    });
    return Task.FromResult(FakeWorker.Page(days, metadata));
};
var coverage = await gateway.ExecuteAsync(caller, Coverage(coverageArgs), default);
Check(coverage.Status == "NotReady" && coverage.Data?.GetProperty("CoveragePercent").GetDecimal() == 50m &&
      !coverage.Data!.Value.GetRawText().Contains("InternalPath") &&
      !coverage.Data.Value.GetRawText().Contains("LastAttempt"), "覆盖率质量未通过并删除内部字段");

fake.Handler = (operation, payload, token) => Task.FromResult(new WorkerReadReply("UnknownQuery", null));
Check((await gateway.ExecuteAsync(caller, Overview(), default)).Status == "Rejected",
      "旧 Worker 不支持时失败关闭，不伪造空数据");
fake.Handler = (operation, payload, token) => Task.FromResult(FakeWorker.Page("[{\"Market\":\"SSE\",\"Ticker\":\"510300\",\"Name\":\"x\",\"AsOf\":null,\"Shares\":null,\"Change1\":null,\"Change5\":null,\"Change20\":null,\"Status\":\"Unknown\",\"CoveredDays\":0,\"Secret\":\"x\"}]", next: 999));
Check((await gateway.ExecuteAsync(caller, Overview(), default)).Code == "UpstreamInvalid",
      "不接受越界或不连续的快照分页游标");
fake.Handler = (operation, payload, token) => throw new BackgroundServiceException(
    "ServiceUnavailable", Guid.NewGuid(), false, "fake");
Check((await gateway.ExecuteAsync(caller, Overview(), default)).Code == "UpstreamUnavailable",
      "后台关闭不自动连接数据库或启用补充采集");

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
fake.Handler = (operation, payload, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(FakeWorker.Page("[]")); };
Check((await gateway.ExecuteAsync(caller, Overview(), cancelled.Token)).Code == "Cancelled",
      "取消可控返回");

Check(fake.Operations.All(x => x is "overview" or "coverage") &&
      fake.Payloads.All(x => !x.Contains("RunNow") && !x.Contains("Save") && !x.Contains("export")),
      "全部可达 IPC 路径均为固定只读业务查询");
Console.WriteLine($"AI offline POC: {passed} assertions passed; no Windows SCM, NuGet or network was exercised.");

sealed class FakeWorker : IWorkerPublicReadClient
{
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
    public static WorkerReadReply Page(string items, string? metadata = null, int? next = null) =>
        new("Ok",JsonSerializer.Serialize(new {Items=items,Snapshot="fixed-poc-snapshot",
            NextOffset=next,Metadata=metadata,ContractVersion=1,TotalRows=next is null ? 2 : 1000}));
}
