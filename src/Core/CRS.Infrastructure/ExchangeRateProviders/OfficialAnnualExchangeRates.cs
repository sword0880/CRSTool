using System.Collections.Concurrent;
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.Domain.Performance;
using CRS.ExchangeRates;
using CRS.Timing;

namespace CRS.Infrastructure;

/// <summary>自动获取年度测算使用的官方报价，并保留经过校验的同年缓存。</summary>
public sealed class OfficialAnnualExchangeRates(string cacheDirectory, HttpClient? client = null, TimeProvider? clock = null,
    Func<int, CancellationToken, Task<CentralParitySnapshot?>>? readScheduled = null)
{
    private const string Source = "国家外汇管理局（数据来自中国外汇交易中心）";
    private const int MaxCacheBytes = 128 * 1024;
    private readonly ConcurrentDictionary<int, CentralParitySnapshot> memory = new();
    private readonly SemaphoreSlim gate = new(1);

    public OfficialAnnualExchangeRates() : this(readScheduled: null) { }

    public OfficialAnnualExchangeRates(Func<int, CancellationToken, Task<CentralParitySnapshot?>>? readScheduled) : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CRS", "exchange-rates"), readScheduled: readScheduled) { }

    internal PerformanceFx[] ReadCachedValuationRates(DateOnly[] dates)
    {
        var rates = new List<PerformanceFx>();
        foreach (var year in dates.Distinct().GroupBy(date => date.Year))
        {
            var cached = Read(year.Key);
            if (cached is null) continue;
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
        var ongoing = year == today.Year;
        var (from, through) = ongoing ? (new DateOnly(year, 1, 1), today) : CentralParitySnapshot.YearEndRange(year);

        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var cached = Read(year);
            var eligible = cached is not null && cached.From == from && cached.Through <= through ? cached : null;
            if (!refresh && !ongoing && eligible?.Quotes[^1].Date != through && readScheduled is not null
                && await readScheduled(year, cancellation).ConfigureAwait(false) is { } scheduled && scheduled.IsYearEnd(year)
                && (eligible is null || scheduled.Quotes[^1].Date > eligible.Quotes[^1].Date
                    || scheduled.Quotes[^1].Date == eligible.Quotes[^1].Date && scheduled.FetchedAt > eligible.FetchedAt))
            {
                cancellation.ThrowIfCancellationRequested();
                memory[year] = scheduled;
                Save(year, scheduled);
                return Build(year, scheduled, ongoing);
            }
            if (!refresh && eligible is not null && eligible.Through == through &&
                (!ongoing || eligible.FetchedAt <= now && now - eligible.FetchedAt < TimeSpan.FromHours(1)))
                return Build(year, eligible, ongoing);
            return await Download(year, from, through, now, ongoing, eligible, cancellation).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public string CachedFingerprint(int year)
    {
        var cached = Read(year) ?? throw new CrsException("当前年度汇率缓存不可用，请重新计算后导出。");
        return Build(year, cached, cached.From.Month == 1).Fingerprint;
    }

    private async Task<IExchangeRateProvider> Download(int year, DateOnly from, DateOnly through, DateTimeOffset now,
        bool ongoing, CentralParitySnapshot? cached, CancellationToken cancellation)
    {
        try
        {
            var entry = await SafeCentralParityFeed.ReadAsync(from, through, now, cancellation, client).ConfigureAwait(false);
            if (!entry.IsValid(year)) throw new InvalidDataException("官方报价缺失或区间不完整。");
            cancellation.ThrowIfCancellationRequested();
            if (cached is not null && cached.Quotes[^1].Date > entry.Quotes[^1].Date) return Build(year, cached, ongoing);
            memory[year] = entry;
            Save(year, entry);
            return Build(year, entry, ongoing);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            if (cached is not null) return Build(year, cached, ongoing);
            throw new CrsException($"暂时无法取得 {year} 年的官方汇率，也没有可用的同年度缓存。请检查网络后重试。");
        }
    }

    private CentralParitySnapshot? Read(int year)
    {
        if (memory.TryGetValue(year, out var cached)) return cached;
        try
        {
            using var file = File.OpenRead(CachePath(year));
            if (file.Length is <= 0 or > MaxCacheBytes) return null;
            var entry = JsonSerializer.Deserialize<CentralParitySnapshot>(file);
            return entry is not null && entry.IsValid(year) ? entry : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void Save(int year, CentralParitySnapshot entry)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            AtomicExport.Write(CachePath(year), stream => JsonSerializer.Serialize(stream, entry), CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private string CachePath(int year) => Path.Combine(cacheDirectory, FormattableString.Invariant($"safe-midpoint-v1-{year}.json"));

    private static IExchangeRateProvider Build(int year, CentralParitySnapshot entry, bool ongoing)
    {
        var latest = entry.Quotes[^1];
        var first = ongoing ? entry.Quotes[0].Date : latest.Date;
        var provisional = ongoing || latest.Date != new DateOnly(year, 12, 31);
        var method = ongoing ? "当年报价日中间价算术平均，供临时测算"
            : provisional ? "年末无当日报价，取该年最后报价，供临时测算" : "年度末日人民币汇率中间价";
        var usd = ongoing ? decimal.Round(entry.Quotes.Average(q => q.Usd), 6, MidpointRounding.AwayFromZero) : latest.Usd;
        var hkd = ongoing ? decimal.Round(entry.Quotes.Average(q => q.Hkd), 6, MidpointRounding.AwayFromZero) : latest.Hkd;
        return new AnnualRates(year,
        [
            new(year, "CNY", 1, "人民币本币", "", "本币折算", null, null, false, true),
            new(year, "HKD", hkd, Source, SafeCentralParityFeed.Url, method, first, latest.Date, provisional, true),
            new(year, "USD", usd, Source, SafeCentralParityFeed.Url, method, first, latest.Date, provisional, true)
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
