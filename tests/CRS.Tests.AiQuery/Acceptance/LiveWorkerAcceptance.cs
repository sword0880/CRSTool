using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.AI.Adapters;
using CRS.AI.Contracts;
using CRS.AI.Infrastructure;
using CRS.AI.Query;
using CRS.Worker.Ipc;

namespace CRS.Tests.AiQuery.Acceptance;

internal static class LiveWorkerAcceptance
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["--live-worker", var market, var ticker, var from, var to])
        {
            Console.Error.WriteLine("用法：--live-worker <SSE|SZSE> <六位代码> <yyyy-MM-dd> <yyyy-MM-dd>");
            return 2;
        }

        // 使用已安装服务的默认管道，复用客户端对实际服务进程的校验。
        using var identity = WindowsIdentity.GetCurrent();
        var subject = identity.User?.Value ?? throw new UnauthorizedAccessException("无法确定当前 Windows 用户。");
        var caller = new AiCaller(subject, true);
        var auditFile = Path.GetTempFileName();
        try
        {
            var worker = new MeasuredWorker(new WorkerPublicReadClient(new BackgroundPipeClient()));
            var gateway = AiReadGatewayFactory.Create(worker, new AiAppendOnlyFileAuditSink(auditFile));
            var mcp = new McpReadToolAdapter(gateway);
            var openAi = new OpenAiFunctionReadAdapter(gateway);
            var overview = new AiCall(AiCatalog.Overview, JsonSerializer.SerializeToElement(new { }));
            var coverage = new AiCall(AiCatalog.Coverage, JsonSerializer.SerializeToElement(new { market, ticker, from, to }));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var success = true;
            foreach (var call in new[] { overview, coverage })
            {
                worker.Reset(forcePagination: true);
                var paged = await mcp.CallToolAsync(caller, call, deadline.Token);
                var forcedPages = worker.Pages;
                worker.Reset(forcePagination: false);
                var normal = await openAi.ExecuteFunctionAsync(caller, call, deadline.Token);
                if (paged.Data is not { } pagedData || normal.Data is not { } normalData ||
                    paged.DataVersion is null || paged.DataVersion != normal.DataVersion ||
                    !JsonElement.DeepEquals(pagedData, normalData) ||
                    paged.Status != normal.Status || paged.Code != normal.Code)
                {
                    Console.Error.WriteLine($"{call.Tool} 验收失败 MCP={paged.Status}/{paged.Code} OpenAI={normal.Status}/{normal.Code}");
                    success = false;
                    continue;
                }

                var rows = call.Tool == AiCatalog.Overview
                    ? normalData.GetArrayLength() : normalData.GetProperty("Days").GetArrayLength();
                if (rows <= 1 || forcedPages < 2)
                {
                    Console.Error.WriteLine($"{call.Tool} 样本未覆盖多页。行数={rows} 实际页数={forcedPages}");
                    success = false;
                }
                Console.WriteLine($"{call.Tool} 状态={normal.Status}/{normal.Code} 行数={rows} 分页={forcedPages}/{worker.Pages} 数据版本={normal.DataVersion}");
                if (call.Tool == AiCatalog.Coverage)
                    Console.WriteLine($"覆盖样本 {market}/{ticker} {from} 至 {to} 接受={normalData.GetProperty("AcceptedDays").GetInt32()} 登记={normalData.GetProperty("RegisteredTradingDays").GetInt32()}");
            }

            var auditLines = (await File.ReadAllLinesAsync(auditFile, deadline.Token)).Length;
            Console.WriteLine($"本地审计事件数={auditLines}");
            return success && auditLines == 8 ? 0 : 1;
        }
        finally
        {
            File.Delete(auditFile);
        }
    }

    // 只在验收中缩小第一页，后续页由正式快照读取逻辑处理。
    private sealed class MeasuredWorker(IWorkerPublicReadClient worker) : IWorkerPublicReadClient
    {
        private bool _forcePagination;
        public int Pages { get; private set; }

        public void Reset(bool forcePagination)
        {
            _forcePagination = forcePagination;
            Pages = 0;
        }

        public async Task<WorkerReadReply> QueryAsync(string operation, string payload, CancellationToken token)
        {
            if (_forcePagination && Pages == 0)
            {
                var request = JsonNode.Parse(payload)!.AsObject();
                request["Count"] = 1;
                payload = request.ToJsonString();
            }
            var result = await worker.QueryAsync(operation, payload, token);
            if (result.Code == "Ok") Pages++;
            return result;
        }
    }
}
