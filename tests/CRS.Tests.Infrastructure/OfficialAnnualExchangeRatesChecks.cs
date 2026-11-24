using System.Text;
using CRS.Domain;
using CRS.ExchangeRates;
using CRS.Infrastructure;

internal static class OfficialAnnualExchangeRatesChecks
{
    internal static async Task RunAsync()
    {
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
        var store = new Dictionary<int, CentralParitySnapshot>();
        Task<CentralParitySnapshot?> Read(int year, CancellationToken _) =>
            Task.FromResult(store.GetValueOrDefault(year));
        async Task<CentralParitySnapshot?> Refresh(int year, CancellationToken token)
        {
            var today = DateOnly.FromDateTime(CRS.Timing.BeijingClock.Normalize(clock.GetUtcNow()).Date);
            var (from, through) = year == today.Year
                ? (new DateOnly(year, 1, 1), today) : CentralParitySnapshot.YearEndRange(year);
            var fresh = await SafeCentralParityFeed.ReadAsync(from, through, clock.GetUtcNow(), token, client);
            if (store.TryGetValue(year, out var prior))
            {
                var quotes = prior.Quotes.Concat(fresh.Quotes).GroupBy(q => q.Date)
                    .Select(group => group.First()).OrderBy(q => q.Date).ToArray();
                fresh = new CentralParitySnapshot(1, from, through, fresh.FetchedAt, quotes);
            }
            store[year] = fresh;
            return fresh;
        }
        var legacySnapshots = new Dictionary<int, CentralParitySnapshot>
        {
            [2022] = new(1, new DateOnly(2022, 12, 18), new DateOnly(2022, 12, 31),
                clock.GetUtcNow(), [new(new DateOnly(2022, 12, 30), 6.7261m, 0.8606m)])
        };
        var oldYearRange = CentralParitySnapshot.YearEndRange(2022);
        store[2022] = new(1, oldYearRange.From, oldYearRange.Through, clock.GetUtcNow(),
            [new(oldYearRange.Through, 7.9999m, 0.9999m)]);
        Task<CentralParitySnapshot?> ReadLegacySnapshot(int year, CancellationToken _) =>
            Task.FromResult(legacySnapshots.GetValueOrDefault(year));
        Task<LegacyAnnualExchangeRateRecord[]> ReadLegacy(int year, CancellationToken _) =>
            Task.FromResult(year == 2021
                ? new[] { new LegacyAnnualExchangeRateRecord(2021, "USD", 6.4515m, "来源未登记", "", "", null, null, false, false) }
                : []);
        Task<bool> ImportLegacy(CentralParitySnapshot snapshot, CancellationToken __)
        {
            legacySnapshots[snapshot.Through.Year] = snapshot;
            return Task.FromResult(true);
        }
        var provider = new OfficialAnnualExchangeRates(Read, Refresh, ReadLegacySnapshot, ReadLegacy, ImportLegacy, NewCachePath(), clock);

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
        var frozenHistory = await provider.LoadAsync(2022, default);
        Check(frozenHistory.Get(2022, "USD").Value == 6.7261m &&
            frozenHistory.Get(2022, "USD").SourceConfirmed == false && requests == 3,
            "迁入的旧历史快照优先于后续官方数据，历史汇率不被重算");
        var recent = await provider.LoadAsync(2026, default);
        Check(recent.Fingerprint == current.Fingerprint && requests == 3, "数据库快照须避免重复查询");
        Check(provider.CachedFingerprint(2025) == closed.Fingerprint, "读取其他年度不得使已完成年度的汇率指纹失效");
        await CheckUnavailable(() => provider.LoadAsync(2024, default), 2024);

        var offlineProvider = new OfficialAnnualExchangeRates(Read,
            (_, _) => throw new HttpRequestException("Synthetic offline"), ReadLegacySnapshot, ReadLegacy, ImportLegacy, NewCachePath(), clock);
        await offlineProvider.LoadAsync(2023, default);
        await offlineProvider.LoadAsync(2025, default);
        await offlineProvider.LoadAsync(2026, default);
        var valuationRates = offlineProvider.ReadCachedValuationRates(
            [new(2023, 12, 31), new(2025, 12, 30), new(2026, 1, 12), new(2026, 1, 14)]);
        Check(valuationRates.Length == 6 &&
            valuationRates.Single(f => f.From == "USD" && f.Date.Year == 2026).Rate == 7.024m &&
            valuationRates.Single(f => f.From == "HKD" && f.Date.Year == 2025).Rate == .90394m &&
            valuationRates.Single(f => f.From == "USD" && f.Date.Year == 2023).Source.RowIdentity == "2023-12-29",
            "估值读取复用数据库报价，不套用税务均值或缓存范围外报价");
        var restored = await offlineProvider.LoadAsync(2026, default, refresh: true);
        Check(restored.Fingerprint == current.Fingerprint, "离线时保留数据库中已确认的同年度汇率");
        await CheckUnavailable(() => new OfficialAnnualExchangeRates(Read,
            (_, _) => throw new HttpRequestException("Synthetic offline"), ReadLegacySnapshot, ReadLegacy, ImportLegacy, NewCachePath(),
            new FixedClock(new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero))).LoadAsync(2026, default), 2026);

        var legacySnapshot = await new OfficialAnnualExchangeRates(Read,
            (_, _) => throw new HttpRequestException("Synthetic offline"), ReadLegacySnapshot, ReadLegacy, ImportLegacy, NewCachePath(), clock)
            .LoadAsync(2022, default);
        Check(legacySnapshot.Get(2022, "USD").Value == 6.7261m &&
            legacySnapshot.Get(2022, "USD").SourceConfirmed == false &&
            legacySnapshot.Get(2022, "USD").Source.Contains("未重新核验"),
            "官方数据不可用时回退已迁入数据库的旧本地快照，保留历史数值及来源状态");
        var legacy = await new OfficialAnnualExchangeRates(Read,
            (_, _) => throw new HttpRequestException("Synthetic offline"), ReadLegacySnapshot, ReadLegacy, ImportLegacy, NewCachePath(), clock)
            .LoadAsync(2021, default);
        Check(legacy.Get(2021, "USD").Value == 6.4515m && legacy.Get(2021, "USD").SourceConfirmed == false,
            "官方数据不可用时回退旧年度数据库值，保留原值及未确认来源标记");

        var migrationDirectory = NewCachePath();
        Directory.CreateDirectory(migrationDirectory);
        var migrationPath = Path.Combine(migrationDirectory, "safe-midpoint-v1-2020.json");
        var migrationRange = CentralParitySnapshot.YearEndRange(2020);
        var migrationSnapshot = new CentralParitySnapshot(1, migrationRange.From, migrationRange.Through,
            clock.GetUtcNow(), [new(migrationRange.Through.AddDays(-1), 7.1234m, 0.8765m)]);
        await File.WriteAllTextAsync(migrationPath, System.Text.Json.JsonSerializer.Serialize(migrationSnapshot));
        var migrating = new OfficialAnnualExchangeRates(Read,
            (_, _) => throw new HttpRequestException("Synthetic offline"), ReadLegacySnapshot, ReadLegacy,
            ImportLegacy, migrationDirectory, clock);
        var migrated = await migrating.LoadAsync(2020, default);
        var migratedValuation = migrating.ReadCachedValuationRates([new DateOnly(2020, 12, 31)]);
        Check(migrated.Get(2020, "USD").Value == 7.1234m && !migrated.Get(2020, "USD").SourceConfirmed &&
            migratedValuation.Single(x => x.From == "USD").Rate == 7.1234m &&
            !File.Exists(migrationPath) && File.Exists(migrationPath + ".migrated"),
            "桌面旧 JSON 先迁入 Worker 数据库，确认成功后归档并保留估值读取能力");

        var scheduledLookups = 0;
        var desktop = new OfficialAnnualExchangeRates((year, token) =>
        {
            scheduledLookups++;
            return Read(year, token);
        }, Refresh, ReadLegacySnapshot, ReadLegacy, ImportLegacy, NewCachePath(), clock);
        var imported = await desktop.LoadAsync(2025, default);
        Check(imported.Fingerprint == closed.Fingerprint && desktop.CachedFingerprint(2025) == closed.Fingerprint,
            "桌面优先读取 Worker 数据库中的同年度快照");
        await desktop.LoadAsync(2025, default);
        Check(scheduledLookups >= 2, "汇率页面重开后继续复用数据库快照");
        Console.WriteLine("官方年度汇率验证通过：年末报价、当年均值、数据库缓存、旧值回退及历史估值隔离。");
    }

    internal static async Task CheckPublicSourceAsync()
    {
        var memory = new Dictionary<int, CentralParitySnapshot>();
        var now = DateTimeOffset.UtcNow;
        async Task<CentralParitySnapshot?> Refresh(int year, CancellationToken token)
        {
            var today = DateOnly.FromDateTime(CRS.Timing.BeijingClock.Normalize(now).Date);
            var (from, through) = year == today.Year
                ? (new DateOnly(year, 1, 1), today) : CentralParitySnapshot.YearEndRange(year);
            var snapshot = await SafeCentralParityFeed.ReadAsync(from, through, now, token);
            memory[year] = snapshot;
            return snapshot;
        }
        var provider = new OfficialAnnualExchangeRates(
            (year, _) => Task.FromResult(memory.GetValueOrDefault(year)), Refresh,
            (_, _) => Task.FromResult<CentralParitySnapshot?>(null),
            (_, _) => Task.FromResult(Array.Empty<LegacyAnnualExchangeRateRecord>()),
            (_, _) => Task.FromResult(true), NewCachePath());
        var yearNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Year;
        foreach (var selected in new[] { yearNow - 1, yearNow })
        {
            var rates = await provider.LoadAsync(selected, default);
            foreach (var rate in rates.ForYear(selected).Where(r => r.Url.Length > 0))
                Console.WriteLine($"{rate.Year} {rate.Currency} {rate.Value} {rate.Start:yyyy-MM-dd} {rate.End:yyyy-MM-dd} {rate.Method}");
        }
    }

    private static async Task CheckUnavailable(Func<Task> load, int year)
    {
        try { await load(); }
        catch (CrsException error) when (error.Message.Contains($"{year} 年") && error.Message.Contains("后台数据库")) { return; }
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
