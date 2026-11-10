using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.ExchangeRates;
using CRS.Timing;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using Quartz;

internal static class AnnualExchangeRateJobChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CRS.AnnualRateJob", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var key = RandomNumberGenerator.GetBytes(32);
        var clock = new DataCleanupChecks.Clock { Value = new(2027, 1, 1, 9, 0, 0, TimeSpan.FromHours(8)) };
        var handler = new Feed();
        using var http = new HttpClient(handler);
        var rates = new WorkerAnnualExchangeRates(root, key, http, clock);
        try
        {
            using var repository = new EncryptedTaskRepository(Path.Combine(root, "scheduler.db"), key);
            await using var catalog = new BackgroundPluginCatalog();
            await using var runtime = new BackgroundRuntime(repository, catalog, annualRates: rates);
            await runtime.StartAsync(default);
            var user = new BackgroundCaller("synthetic-reader", false);
            var plans = await runtime.HandleAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetJobs"), user, default);
            var plan = plans.Jobs!.Single();
            var cron = new CronExpression(plan.Cron, TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId));
            Check(plan is { Enabled: true, Cron: "0 0 9 1 1 ?", TimeZoneId: BeijingClock.ZoneId }
                && cron.GetNextValidTimeAfter(clock.Value.AddSeconds(-1)) == clock.Value
                && cron.GetNextValidTimeAfter(clock.Value) == clock.Value.AddYears(1), "Cron 每年在北京时间 1 月 1 日 09:00 触发");

            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!repository.History().Any(x => x.Result == "Succeeded")) await Task.Delay(50, wait.Token);
            var request = new BackgroundRequest(WorkerProtocol.Version, Guid.NewGuid(), WorkerProtocol.GetAnnualExchangeRates, Payload: "2026");
            var reply = await runtime.HandleAsync(request, user, default);
            var quote = JsonSerializer.Deserialize<CentralParitySnapshot>(reply.Payload!)!;
            Check(reply.Code == "Ok" && quote.IsYearEnd(2026) && quote.Quotes[^1] == new CentralParityQuote(new(2026, 12, 31), 7.02m, .901m)
                && handler.Requests == 1 && handler.Query == "?startDate=2026-12-18&endDate=2026-12-31&queryYN=true",
                "重启补跑查询上一年年末，同一来源报价通过只读管道命令提供给普通用户");
            Check((await runtime.HandleAsync(request with { Payload = "2027" }, user, default)).Code == "InvalidRequest"
                && (await runtime.HandleAsync(request with { Payload = "2025" }, user, default)).Code == "NotAvailable"
                && (await runtime.HandleAsync(request with { Cursor = "file" }, user, default)).Code == "InvalidRequest",
                "拒绝未结束年度和无关参数，缺少的年度无结果");
            var restarted = new WorkerAnnualExchangeRates(root, key, http, clock);
            Check(!restarted.NeedsCatchUp(plan) && (await restarted.ExecuteAsync(plan.JobId, default)).Status == "Succeeded"
                && handler.Requests == 1, "已落盘年末报价重启后可用且无需重复下载");

            clock.Value = clock.Value.AddYears(1);
            handler.QuoteDay = new(2027, 12, 30);
            var provisional = await restarted.ExecuteAsync(plan.JobId, default);
            Check(provisional is { Status: "NotPublished", RetryAfter: not null } && provisional.Checkpoint!.Contains("临时测算"),
                "当日中间价缺失时保存最后报价并安排复查");
            handler.QuoteDay = new(2027, 12, 31);
            clock.Value = clock.Value.AddDays(1);
            Check((await restarted.ExecuteAsync(plan.JobId, default)).Status == "Succeeded"
                && !restarted.NeedsCatchUp(plan), "后续复查使用补发的年度末日报价");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS 年度汇率：" + message);
    }

    private sealed class Feed : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Query { get; private set; }
        public DateOnly QuoteDay { get; set; } = new(2026, 12, 31);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Query = request.RequestUri!.Query;
            var day = QuoteDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<table><tr><th>日期</th><th>美元</th><th>港元</th></tr><tr><td>{day}</td><td>702</td><td>90.1</td></tr></table>")
            });
        }
    }
}
