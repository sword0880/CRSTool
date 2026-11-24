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
        try
        {
            using var repository = new EncryptedTaskRepository(Path.Combine(root, "scheduler.db"), key);
            var rates = new WorkerAnnualExchangeRates(repository, root, key, http, clock);
            var legacy = repository.ReadLegacyAnnualRates(2026);
            var cny2026 = legacy.Single(x => x.Currency == "CNY");
            Check(legacy.Length == 3 && legacy.Single(x => x.Currency == "USD").Value == 6.852873m &&
                legacy.Single(x => x.Currency == "HKD").Value == 0.875157m && cny2026.Value == 1m &&
                cny2026.Url == "https://www.chinamoney.com.cn/chinese/bkccpr/?tab=2" &&
                cny2026.Method == "报价日中间价算术平均；非报价日不补值；四舍五入保留六位小数" &&
                cny2026.Start == "2026-01-01" && cny2026.End == "2026-10-08" && !cny2026.Provisional &&
                cny2026.FirstObservation == "2026-01-05" && cny2026.ObservationCount == 182 &&
                cny2026.UsdSum == "1247.2229" && cny2026.HkdSum == "159.27864" &&
                repository.ReadLegacyAnnualRates(2021).Single(x => x.Currency == "USD").Value == 6.4515m &&
                repository.ReadLegacyAnnualRates(2022).Single(x => x.Currency == "HKD").Value == 0.8606m &&
                repository.ReadLegacyAnnualRates(2023).Single(x => x.Currency == "USD").Value == 7.0809m &&
                repository.ReadLegacyAnnualRates(2024).Single(x => x.Currency == "HKD").Value == 0.9260m &&
                repository.ReadLegacyAnnualRates(2025).Single(x => x.Currency == "USD").Value == 7.1800m,
                "旧年度 JSON 汇率数值及 2026 年来源元数据按原值导入 SQLCipher");
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
            Check((await runtime.HandleAsync(request with { Payload = "2027" }, user, default)).Code == "NotAvailable"
                && (await runtime.HandleAsync(request with { Payload = "2025" }, user, default)).Code == "NotAvailable"
                && (await runtime.HandleAsync(request with { Cursor = "file" }, user, default)).Code == "InvalidRequest",
                "当前年度尚无报价和缺少的历史年度返回无数据，无关参数被拒绝");
            var restarted = new WorkerAnnualExchangeRates(repository, root, key, http, clock);
            var stored = repository.ReadAnnualExchangeRateSnapshot(2026);
            var (yearStart, yearEnd) = CentralParitySnapshot.YearEndRange(2026);
            var revised = new CentralParitySnapshot(1, yearStart, yearEnd, clock.GetUtcNow().AddDays(1),
                [new(yearEnd, 7.99m, 0.999m), new(yearEnd.AddDays(-1), 7.01m, 0.899m)]);
            var merged = repository.SaveAnnualExchangeRateSnapshot(2026, revised);
            Check(merged.Quotes.Single(q => q.Date == yearEnd).Usd == 7.02m &&
                merged.Quotes.Any(q => q.Date == yearEnd.AddDays(-1)),
                "同一报价日的后续修订不覆盖首次汇率，新日期只追加");
            var (legacyFrom, legacyThrough) = CentralParitySnapshot.YearEndRange(2025);
            var desktopLegacy = new CentralParitySnapshot(1, legacyFrom, legacyThrough, clock.GetUtcNow(),
                [new(legacyThrough, 7.18m, 0.921m)]);
            var import = new BackgroundRequest(WorkerProtocol.Version, Guid.NewGuid(),
                WorkerProtocol.ImportAnnualExchangeRateSnapshot, Payload: JsonSerializer.Serialize(desktopLegacy));
            var imported = await runtime.HandleAsync(import, user, default);
            var sameUser = await runtime.HandleAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
                WorkerProtocol.GetLegacyAnnualExchangeRateSnapshot, Payload: "2025"), user, default);
            var otherUser = await runtime.HandleAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
                WorkerProtocol.GetLegacyAnnualExchangeRateSnapshot, Payload: "2025"),
                new BackgroundCaller("synthetic-other", false), default);
            Check(imported.Code == "Ok" && sameUser.Code == "Ok" && otherUser.Code == "NotAvailable",
                "旧桌面快照迁入数据库后按 Windows SID 隔离");
            Check(stored is not null && stored.IsYearEnd(2026) &&
                !restarted.NeedsCatchUp(plan) && (await restarted.ExecuteAsync(plan.JobId, default)).Status == "Succeeded"
                && handler.Requests == 1, "SQLCipher 年末报价重启后可用且无需重复下载");

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
