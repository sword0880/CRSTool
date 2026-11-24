using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Domain.Performance;
using CRS.ExchangeRates;
using CRS.Timing;

namespace CRS.Infrastructure;

/// <summary>桌面汇率只读 Worker SQLCipher；本机 JSON 仅作为一次性迁移输入。</summary>
public sealed class OfficialAnnualExchangeRates(
    Func<int, CancellationToken, Task<CentralParitySnapshot?>> readOfficial,
    Func<int, CancellationToken, Task<CentralParitySnapshot?>> refreshOfficial,
    Func<int, CancellationToken, Task<CentralParitySnapshot?>> readLegacySnapshot,
    Func<int, CancellationToken, Task<LegacyAnnualExchangeRateRecord[]>> readLegacy,
    Func<CentralParitySnapshot, CancellationToken, Task<bool>> importLegacy,
    string? legacyCacheDirectory = null,
    TimeProvider? clock = null)
{
    private const string Source = "国家外汇管理局（数据来自中国外汇交易中心）";
    private const int MaxLegacyCacheBytes = 128 * 1024;
    private readonly ConcurrentDictionary<int, CentralParitySnapshot> memory = new();
    private readonly ConcurrentDictionary<int, IExchangeRateProvider> loadedProviders = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> valuationRetryAfter = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly string legacyDirectory = legacyCacheDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CRS", "exchange-rates");

    internal PerformanceFx[] ReadCachedValuationRates(DateOnly[] dates)
    {
        // 估值接口是同步契约；缺少内存快照时在后台线程通过只读 IPC 懒加载，避免回退到桌面 JSON。
        var years = dates.Select(date => date.Year).Distinct().Where(year => !memory.ContainsKey(year)).ToArray();
        if (years.Length != 0)
            Task.Run(() => Parallel.ForEach(years, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                TryLoadSnapshotForValuation)).GetAwaiter().GetResult();

        var rates = new List<PerformanceFx>();
        foreach (var year in dates.Distinct().GroupBy(date => date.Year))
        {
            if (!memory.TryGetValue(year.Key, out var cached)) continue;
            foreach (var date in year.Where(date => date >= cached.From && date <= cached.Through))
            {
                var quote = cached.Quotes.LastOrDefault(q => q.Date <= date);
                if (quote is null || date.DayNumber - quote.Date.DayNumber > 14) continue;
                var source = new PerformanceSource(SafeCentralParityFeed.Url, quote.Date.ToString("yyyy-MM-dd"));
                rates.Add(new($"SAFE:USD:{date.DayNumber}", "USD", "CNY", date, quote.Usd, source, PerformanceQuality.Reconstructed));
                rates.Add(new($"SAFE:HKD:{date.DayNumber}", "HKD", "CNY", date, quote.Hkd, source, PerformanceQuality.Reconstructed));
            }
        }
        return rates.ToArray();
    }

    public async Task<IExchangeRateProvider> LoadAsync(int year, CancellationToken cancellation, bool refresh = false)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var today = DateOnly.FromDateTime(BeijingClock.Normalize(now).Date);
        if (year < 2000 || year > today.Year) throw new CrsException($"请选择 2000 至 {today.Year} 年之间的年度。");

        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // 迁移完成并收到数据库确认后，才将旧缓存改名归档。
            await ImportLegacyCacheAsync(year, cancellation).ConfigureAwait(false);
            var ongoing = year == today.Year;
            var legacySnapshot = await readLegacySnapshot(year, cancellation).ConfigureAwait(false);
            // 已结束年度的旧本地快照是历史兼容事实；迁入后冻结其数值，不被后续官方刷新覆盖。
            if (!ongoing && legacySnapshot is not null && ValidSnapshot(year, legacySnapshot, today))
            {
                memory[year] = legacySnapshot;
                var historical = Build(year, legacySnapshot, ongoing: false, legacy: true);
                loadedProviders[year] = historical;
                return historical;
            }
            var cached = await readOfficial(year, cancellation).ConfigureAwait(false);
            if (cached is not null && !ValidSnapshot(year, cached, today)) cached = null;
            var through = ongoing ? today : CentralParitySnapshot.YearEndRange(year).Through;
            var shouldRefresh = refresh || cached is null ||
                (ongoing && (cached.Through != through || cached.FetchedAt > now || now - cached.FetchedAt >= TimeSpan.FromHours(1))) ||
                (!ongoing && cached.Quotes[^1].Date != through);
            if (shouldRefresh)
            {
                try
                {
                    var updated = await refreshOfficial(year, cancellation).ConfigureAwait(false);
                    if (updated is not null && ValidSnapshot(year, updated, today)) cached = updated;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    // 已有数据库快照优先保留；无官方快照时才读取旧版年度汇率兼容记录。
                }
            }
            if (cached is not null)
            {
                memory[year] = cached;
                var provider = Build(year, cached, ongoing);
                loadedProviders[year] = provider;
                return provider;
            }

            // 当年旧缓存只在官方数据暂不可用时作为临时回退。
            if (legacySnapshot is not null && ValidSnapshot(year, legacySnapshot, today))
            {
                memory[year] = legacySnapshot;
                var provider = Build(year, legacySnapshot, ongoing, legacy: true);
                loadedProviders[year] = provider;
                return provider;
            }

            // 历史手工年度汇率只作旧版兼容，不覆盖已取得的官方报价。
            var legacy = await readLegacy(year, cancellation).ConfigureAwait(false);
            if (legacy.Length != 0)
            {
                var provider = new LegacyAnnualRates(legacy);
                loadedProviders[year] = provider;
                return provider;
            }
            throw new CrsException($"后台数据库中没有 {year} 年汇率，官方报价也暂时不可用。请检查后台服务后重试。");
        }
        finally { gate.Release(); }
    }

    private void TryLoadSnapshotForValuation(int year)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (valuationRetryAfter.TryGetValue(year, out var retryAt) && now < retryAt) return;
        var today = DateOnly.FromDateTime(BeijingClock.Normalize(now).Date);
        var snapshot = year < today.Year ? ReadSnapshotForValuation(readLegacySnapshot, year) : null;
        if (snapshot is null || !ValidSnapshot(year, snapshot, today))
            snapshot = ReadSnapshotForValuation(readOfficial, year);
        if (snapshot is null || !ValidSnapshot(year, snapshot, today))
            snapshot = ReadSnapshotForValuation(readLegacySnapshot, year);
        if (snapshot is not null && ValidSnapshot(year, snapshot, today))
        {
            memory[year] = snapshot;
            valuationRetryAfter.TryRemove(year, out _);
            return;
        }
        // 估值只读取缓存，不在此触发网络刷新；短暂失败后一分钟再试。
        valuationRetryAfter[year] = now.AddMinutes(1);
    }

    private static CentralParitySnapshot? ReadSnapshotForValuation(
        Func<int, CancellationToken, Task<CentralParitySnapshot?>> read, int year)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { return Task.Run(() => read(year, deadline.Token)).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or HttpRequestException)
        { return null; }
    }

    public string CachedFingerprint(int year)
    {
        if (loadedProviders.TryGetValue(year, out var provider)) return provider.Fingerprint;
        if (memory.TryGetValue(year, out var cached)) return Build(year, cached, cached.From.Month == 1).Fingerprint;
        throw new CrsException("当前年度汇率尚未从后台数据库读取，请重新打开汇率管理页面。");
    }

    private async Task ImportLegacyCacheAsync(int year, CancellationToken token)
    {
        var path = Path.Combine(legacyDirectory, FormattableString.Invariant($"safe-midpoint-v1-{year}.json"));
        if (!File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaxLegacyCacheBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            try
            {
                var snapshot = JsonSerializer.Deserialize<CentralParitySnapshot>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var today = DateOnly.FromDateTime(BeijingClock.Normalize((clock ?? TimeProvider.System).GetUtcNow()).Date);
                if (snapshot is null || !ValidSnapshot(year, snapshot, today)) return;
                if (!await importLegacy(snapshot, token).ConfigureAwait(false)) return;
                var archive = path + ".migrated";
                if (!File.Exists(archive)) File.Move(path, archive);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // 导入失败时保留源文件；下次读取可安全重试。
        }
    }

    private static bool ValidSnapshot(int year, CentralParitySnapshot snapshot, DateOnly today)
    {
        if (!snapshot.IsValid(year)) return false;
        if (year < today.Year) return snapshot.IsYearEnd(year);
        return snapshot.From == new DateOnly(year, 1, 1) && snapshot.Through <= today;
    }

    private static IExchangeRateProvider Build(int year, CentralParitySnapshot entry, bool ongoing, bool legacy = false)
    {
        var latest = entry.Quotes[^1];
        var first = ongoing ? entry.Quotes[0].Date : latest.Date;
        var provisional = ongoing || latest.Date != new DateOnly(year, 12, 31);
        var method = ongoing ? "当年报价日中间价算术平均，供临时测算"
            : provisional ? "年末无当日报价，取该年最后报价，供临时测算" : "年度末日人民币汇率中间价";
        var source = legacy ? "旧版本地官方汇率缓存（已迁入数据库，未重新核验）" : Source;
        var usd = ongoing ? decimal.Round(entry.Quotes.Average(q => q.Usd), 6, MidpointRounding.AwayFromZero) : latest.Usd;
        var hkd = ongoing ? decimal.Round(entry.Quotes.Average(q => q.Hkd), 6, MidpointRounding.AwayFromZero) : latest.Hkd;
        return new AnnualRates(year,
        [
            new(year, "CNY", 1, "人民币本币", "", "本币折算", null, null, false, true),
            new(year, "HKD", hkd, source, SafeCentralParityFeed.Url, method, first, latest.Date, provisional, !legacy),
            new(year, "USD", usd, source, SafeCentralParityFeed.Url, method, first, latest.Date, provisional, !legacy)
        ]);
    }

    private sealed class AnnualRates(int year, AppliedRate[] rates) : IExchangeRateProvider
    {
        public string Fingerprint { get; } = XmlFields.HashJson(rates);
        public List<AppliedRate> ForYear(int requestedYear) => requestedYear == year ? [.. rates] : [];
        public AppliedRate Get(int requestedYear, string currency) => ForYear(requestedYear).FirstOrDefault(r => r.Currency == currency)
            ?? throw new CrsException($"官方年度汇率未覆盖 {requestedYear} 年 {currency}。");
    }
}

/// <summary>旧版年度汇率只读适配器；数值和来源字段保持迁移前原样。</summary>
internal sealed class LegacyAnnualRates : IExchangeRateProvider
{
    private readonly AppliedRate[] rates;
    public string Fingerprint { get; }

    internal LegacyAnnualRates(IReadOnlyList<LegacyAnnualExchangeRateRecord> rows)
    {
        rates = rows.Select(row => new AppliedRate(row.Year, row.Currency, row.Value,
            row.Source, row.Url, row.Method,
            row.Start is null ? null : DateOnly.ParseExact(row.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            row.End is null ? null : DateOnly.ParseExact(row.End, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            row.Provisional, row.SourceConfirmed)).ToArray();
        Fingerprint = XmlFields.HashJson(rates);
    }

    // 仅用于无后台服务的桌面冒烟测试；生产运行从 Worker SQLCipher 读取同一批历史资料。
    internal static LegacyAnnualRates CreateDefault()
    {
        const string unknown = "来源未登记";
        List<LegacyAnnualExchangeRateRecord> rows =
        [
            new(2021,"CNY",1,"人民币本币","","本币折算",null,null,false,true),
            new(2021,"HKD",0.8291m,unknown,"","",null,null,false,false),
            new(2021,"USD",6.4515m,unknown,"","",null,null,false,false),
            new(2022,"CNY",1,"人民币本币","","本币折算",null,null,false,true),
            new(2022,"HKD",0.8606m,unknown,"","",null,null,false,false),
            new(2022,"USD",6.7261m,unknown,"","",null,null,false,false),
            new(2023,"CNY",1,"人民币本币","","本币折算",null,null,false,true),
            new(2023,"HKD",0.9056m,unknown,"","",null,null,false,false),
            new(2023,"USD",7.0809m,unknown,"","",null,null,false,false),
            new(2024,"CNY",1,"人民币本币","","本币折算",null,null,false,true),
            new(2024,"HKD",0.9260m,unknown,"","",null,null,false,false),
            new(2024,"USD",7.2200m,unknown,"","",null,null,false,false),
            new(2025,"CNY",1,"人民币本币","","本币折算",null,null,false,true),
            new(2025,"HKD",0.9210m,unknown,"","",null,null,false,false),
            new(2025,"USD",7.1800m,unknown,"","",null,null,false,false),
            new(2026,"CNY",1,"人民币本币","","本币折算",null,null,false,true),
            new(2026,"HKD",0.875157m,"中国货币网／中国外汇交易中心人民币汇率中间价历史数据",
                "https://www.chinamoney.com.cn/chinese/bkccpr/?tab=2","报价日中间价算术平均；非报价日不补值；四舍五入保留六位小数",
                "2026-01-01","2026-10-08",true,true,"2026-01-05",182,"1247.2229","159.27864"),
            new(2026,"USD",6.852873m,"中国货币网／中国外汇交易中心人民币汇率中间价历史数据",
                "https://www.chinamoney.com.cn/chinese/bkccpr/?tab=2","报价日中间价算术平均；非报价日不补值；四舍五入保留六位小数",
                "2026-01-01","2026-10-08",true,true,"2026-01-05",182,"1247.2229","159.27864")
        ];
        return new LegacyAnnualRates(rows);
    }

    public List<AppliedRate> ForYear(int requestedYear) => rates.Where(rate => rate.Year == requestedYear).ToList();
    public AppliedRate Get(int requestedYear, string currency) => ForYear(requestedYear).FirstOrDefault(rate => rate.Currency == currency)
        ?? throw new CrsException($"缺少 {requestedYear} 年 {currency} 历史汇率。");
}
