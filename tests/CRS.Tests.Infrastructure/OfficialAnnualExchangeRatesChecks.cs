using System.Text;
using CRS.Domain;
using CRS.Infrastructure;

internal static class OfficialAnnualExchangeRatesChecks
{
    internal static async Task RunAsync()
    {
        var root = NewCachePath();
        var clock = new FixedClock(new DateTimeOffset(2026, 1, 12, 17, 0, 0, TimeSpan.Zero));
        var requests = 0;
        using var client = new HttpClient(new StubHandler(uri =>
        {
            requests++;
            Check(uri.Host == "www.safe.gov.cn" && uri.AbsolutePath == "/AppStructured/hlw/RMBQuery.do", "官方请求地址不符");
            return uri.Query switch
            {
                "?startDate=2025-12-18&endDate=2025-12-31&queryYN=true" => Table(
                    Row("2025-12-31", "702.88", "90.322") + Row("2025-12-30", "703.49", "90.394")),
                "?startDate=2023-12-18&endDate=2023-12-31&queryYN=true" => Table(Row("2023-12-29", "708.27", "90.7")),
                "?startDate=2026-01-01&endDate=2026-01-13&queryYN=true" => Table(
                    Row("2026-01-12", "702.4", "90.4") + Row("2026-01-05", "700", "90.1")),
                "?startDate=2024-12-18&endDate=2024-12-31&queryYN=true" => Table(
                    Row("2024-12-31", "720", "92") + Row("2024-12-31", "721", "93")),
                _ => throw new InvalidOperationException($"请求日期不符 {uri.Query}")
            };
        }));
        var provider = new OfficialAnnualExchangeRates(root, client, clock);
        var closed = await provider.LoadAsync(2025, default);
        Check(closed.Get(2025, "USD") is { Value: 7.0288m, End: var end, Provisional: false, SourceConfirmed: true }
            && end == new DateOnly(2025, 12, 31), "年末中间价或报价日期不符");
        Check(closed.Get(2025, "HKD").Value == .90322m && closed.Get(2025, "CNY").Value == 1m
            && closed.ForYear(2026).Count == 0, "换算单位或年度隔离不符");

        var holiday = await provider.LoadAsync(2023, default);
        Check(holiday.Get(2023, "USD") is { Value: 7.0827m, End: var last, Provisional: true }
            && last == new DateOnly(2023, 12, 29), "年末没有当日报价时须保留实际日期和临时标记");
        var current = await provider.LoadAsync(2026, default);
        Check(current.Get(2026, "USD") is { Value: 7.012m, Start: var first, End: var latest, Provisional: true }
            && first == new DateOnly(2026, 1, 5) && latest == new DateOnly(2026, 1, 12), "当年须使用各报价日均值");
        var recent = await provider.LoadAsync(2026, default);
        Check(recent.Fingerprint == current.Fingerprint && requests == 3, "近期缓存须避免重复查询");
        Check(provider.CachedFingerprint(2025) == closed.Fingerprint, "查询其他年度不得使已完成年度的汇率指纹失效");
        await CheckUnavailable(() => provider.LoadAsync(2024, default), 2024);

        using var offline = new HttpClient(new StubHandler(_ => throw new HttpRequestException("Synthetic offline")));
        var restarted = new OfficialAnnualExchangeRates(root, offline, clock);
        var valuationRates = restarted.ReadCachedValuationRates(
            [new(2023, 12, 31), new(2025, 12, 30), new(2026, 1, 12), new(2026, 1, 14)]);
        Check(valuationRates.Length == 6 &&
            valuationRates.Single(f => f.From == "USD" && f.Date.Year == 2026).Rate == 7.024m &&
            valuationRates.Single(f => f.From == "HKD" && f.Date.Year == 2025).Rate == .90394m &&
            valuationRates.Single(f => f.From == "USD" && f.Date.Year == 2023).Source.RowIdentity == "2023-12-29",
            "年度资产须复用估值日或之前的最近报价，不能套用税务均值或缓存范围外的报价");
        var restored = await restarted.LoadAsync(2026, default, refresh: true);
        Check(restored.Fingerprint == current.Fingerprint, "离线时须恢复同年度的已校验缓存");
        await CheckUnavailable(() => restarted.LoadAsync(2024, default), 2024);
        var nextYear = new OfficialAnnualExchangeRates(root, offline,
            new FixedClock(new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero)));
        await CheckUnavailable(() => nextYear.LoadAsync(2026, default), 2026);
        Console.WriteLine("官方年度汇率验证通过：单位、年末报价、当年均值、无效报价及跨年度缓存隔离。");
    }

    internal static async Task CheckPublicSourceAsync()
    {
        var provider = new OfficialAnnualExchangeRates(NewCachePath());
        var year = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Year;
        foreach (var selected in new[] { 2025, year })
        {
            var rates = await provider.LoadAsync(selected, default);
            foreach (var rate in rates.ForYear(selected).Where(r => r.Url.Length > 0))
                Console.WriteLine($"{rate.Year} {rate.Currency} {rate.Value} {rate.Start:yyyy-MM-dd} {rate.End:yyyy-MM-dd} {rate.Method}");
        }
    }

    private static async Task CheckUnavailable(Func<Task> load, int year)
    {
        try { await load(); }
        catch (CrsException error) when (error.Message.Contains($"{year} 年") && error.Message.Contains("同年度缓存")) { return; }
        throw new InvalidOperationException($"{year} 年缺少合格官方数据时应提示不可用");
    }

    private static string NewCachePath() => Path.Combine(Path.GetTempPath(), "CRS.OfficialRates", Guid.NewGuid().ToString("N"));

    private static string Table(string rows) =>
        "<table><tr><td><table><tr><th>日期</th><th>欧元</th>" +
        $"<th><!-- <s:property value='currency'/>&nbsp; -->港元</th><th>美元</th></tr>{rows}</table></td></tr></table>";

    private static string Row(string day, string usd, string hkd) =>
        $"<tr><td>{day}</td><td>840</td><td>{hkd}</td><td>{usd}</td></tr>";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHandler(Func<Uri, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response(request.RequestUri!), Encoding.UTF8, "text/html")
            });
    }
}
