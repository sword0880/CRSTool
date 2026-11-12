using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Query;
using CRS.Tests.AiQuery.Fixtures;

namespace CRS.Tests.AiQuery.Checks;

internal static class AdversarialReadChecks
{
    private static readonly AiCaller Caller = new("adversarial-check", true);
    private static readonly AiCall Overview = new(AiCatalog.Overview, JsonSerializer.SerializeToElement(new { }));

    public static async Task RunAsync(Action<bool, string> check)
    {
        await CheckAdmissionAsync(check);
        await CheckAuditFailureAsync(check);
        await CheckCoverageAsync(check);
        await CheckSinglePageAsync(check);
    }

    private static async Task CheckAdmissionAsync(Action<bool, string> check)
    {
        var worker = new FakeWorker();
        var audit = new FakeAuditSink();
        var gateway = AiReadGatewayFactory.Create(worker, audit, new AiReadPolicyLimits { MaxTotalCallsPerMinute = 1 });
        for (var i = 0; i < 50; i++)
            await gateway.ExecuteAsync(new AiCaller("unauthorized-" + i, false), Overview, default);
        check(audit.Entries.Count == 0 && worker.Calls == 0, "越权流量无法耗尽审计或调用 Worker");

        var allowed = await gateway.ExecuteAsync(Caller, Overview, default);
        var extra = await gateway.ExecuteAsync(new AiCaller("other-authorized-subject", true), Overview, default);
        check(allowed.Data is not null && extra.Code == "BudgetExceeded" &&
              audit.Entries.Count == 2 && worker.Calls == 1, "全局预算覆盖多个已授权主体");
    }

    private static async Task CheckAuditFailureAsync(Action<bool, string> check)
    {
        using var sink = new BlockingAuditSink();
        var worker = new FakeWorker();
        var gateway = AiReadGatewayFactory.Create(worker, sink);
        try
        {
            var first = Task.Run(() => gateway.ExecuteAsync(Caller, Overview, default));
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check((await first.WaitAsync(TimeSpan.FromSeconds(5))).Code == "AuditUnavailable" && worker.Calls == 0,
                "同步阻塞的审计写入会按时失败，Worker 未被调用");
            check((await gateway.ExecuteAsync(Caller, Overview, default).WaitAsync(TimeSpan.FromSeconds(5)))
                      .Code == "AuditUnavailable" && sink.Calls == 1,
                "审计超时后持续请求不会累积后台写入");
        }
        finally
        {
            sink.Release.Set();
            if (sink.Calls > 0) await sink.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        using var cancel = new CancellationTokenSource();
        var outcome = new CancellingOutcomeSink(cancel);
        var result = await AiReadGatewayFactory.Create(new FakeWorker(), outcome)
            .ExecuteAsync(Caller, Overview, cancel.Token);
        check(result.Code == "Cancelled" && result.Data is null && result.DataVersion is null,
            "结果审计期间取消时，适配器不能收到旧数据");
    }

    private static async Task CheckCoverageAsync(Action<bool, string> check)
    {
        const string metadata = """
            {"Market":"SSE","Ticker":"510300","From":"2026-11-02","To":"2026-11-03","RegisteredTradingDays":2,
             "AcceptedDays":2,"MissingDays":0,"PendingDays":0,"CriticalDays":0,"RejectedDays":0,
             "CoveragePercent":100,"CalendarVerified":true,"CalendarRangeCovered":true,"State":"CompleteRegisteredCalendar"}
            """;
        const string days = """[{"Date":"2026-11-02","State":"Accepted"},{"Date":"2026-11-03","State":"Accepted"}]""";
        using var arguments = JsonDocument.Parse("""{"market":"SSE","ticker":"510300","from":"2026-11-02","to":"2026-11-03"}""");
        var call = new AiCall(AiCatalog.Coverage, arguments.RootElement);
        var worker = new FakeWorker();
        var gateway = AiReadGatewayFactory.Create(worker, new FakeAuditSink());
        async Task<AiResult> ReadAsync(string rows, string summary)
        {
            worker.Handler = (_, _, _) => Task.FromResult(FakeWorker.Page(rows, summary));
            return await gateway.ExecuteAsync(Caller, call, default);
        }

        check((await ReadAsync(days, metadata)).Code == "RegisteredCalendarOnly", "明细与汇总一致的登记日历可读取");
        foreach (var (rows, summary, reason) in new[]
        {
            ("[]", metadata, "汇总声称完整但无明细"),
            (days.Replace("2026-11-03", "2026-11-02"), metadata, "重复交易日"),
            (days.Replace("2026-11-03", "2026-11-04"), metadata, "明细越界"),
            (days.Replace("Accepted", "Missing"), metadata, "逐日状态与汇总矛盾"),
            (days, metadata.Replace(":100", ":50"), "覆盖百分比错误"),
            (days, metadata.Replace("\"CalendarVerified\":true", "\"CalendarVerified\":false"), "未验证日历声称完整")
        })
            check((await ReadAsync(rows, summary)).Code == "UpstreamInvalid", "覆盖结果拒绝" + reason);
    }

    private static async Task CheckSinglePageAsync(Action<bool, string> check)
    {
        var worker = new FakeWorker { UsesSinglePage = true };
        var gateway = AiReadGatewayFactory.Create(worker, new FakeAuditSink());
        worker.Handler = (_, _, _) => Task.FromResult(FakeWorker.Page("[]", next: 1, total: 2, singlePage: true));
        check((await gateway.ExecuteAsync(Caller, Overview, default)).Code == "UpstreamInvalid" &&
              worker.Calls == 1, "专用管道发现分页响应立即拒绝，不请求续页");
        worker.Handler = (_, _, _) => Task.FromResult(FakeWorker.Page("[]", total: 0));
        check((await gateway.ExecuteAsync(Caller, Overview, default)).Code == "UpstreamInvalid", "专用管道拒绝未经确认的旧协议响应");
        worker.Handler = (_, _, _) => Task.FromResult(FakeWorker.Page("[]", total: 0, singlePage: true));
        check((await gateway.ExecuteAsync(Caller, Overview, default)).Status == "Partial", "专用管道接收完整有界单页");
    }

    private sealed class BlockingAuditSink : IAiReadAuditSink, IDisposable
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public Task WriteAsync(AiReadAuditEvent entry, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
            Exited.TrySetResult();
            return Task.CompletedTask;
        }

        public void Dispose() => Release.Dispose();
    }

    private sealed class CancellingOutcomeSink(CancellationTokenSource cancel) : IAiReadAuditSink
    {
        public Task WriteAsync(AiReadAuditEvent entry, CancellationToken token)
        {
            if (entry.Phase == "Outcome") cancel.Cancel();
            return Task.CompletedTask;
        }
    }
}
