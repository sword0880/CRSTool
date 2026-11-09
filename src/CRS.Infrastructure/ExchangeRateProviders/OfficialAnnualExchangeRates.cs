using System.Collections.Concurrent;
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.Timing;

namespace CRS.Infrastructure;

/// <summary>自动获取年度测算使用的官方报价，并保留经过校验的同年缓存。</summary>
public sealed class OfficialAnnualExchangeRates(string cacheDirectory, HttpClient? client = null, TimeProvider? clock = null)
{
    private const string Source = "国家外汇管理局（数据来自中国外汇交易中心）";
    private const int MaxCacheBytes = 128 * 1024;
    private static readonly HttpClient PublicClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly ConcurrentDictionary<int, CacheEntry> memory = new();
    private readonly SemaphoreSlim gate = new(1);

    private sealed record CacheEntry(int Version, DateOnly From, DateOnly Through, DateTimeOffset FetchedAt, CentralParityQuote[] Quotes);

    public async Task<IExchangeRateProvider> LoadAsync(int year, CancellationToken cancellation, bool refresh = false)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var today = DateOnly.FromDateTime(BeijingClock.Normalize(now).Date);
        if (year < 2000 || year > today.Year) throw new CrsException($"请选择 2000 至 {today.Year} 年之间的年度。");
        var ongoing = year == today.Year;
        var through = ongoing ? today : new DateOnly(year, 12, 31);
        var from = ongoing ? new DateOnly(year, 1, 1) : through.AddDays(-13);

        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var cached = Read(year);
            var eligible = cached is not null && cached.From == from && cached.Through <= through ? cached : null;
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
        bool ongoing, CacheEntry? cached, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var quotes = await SafeCentralParityFeed.ReadAsync(client ?? PublicClient, from, through, timeout.Token).ConfigureAwait(false);
            var entry = new CacheEntry(1, from, through, now, quotes);
            if (!IsValid(entry, year)) throw new CrsException("官方报价缺失或区间不完整。");
            cancellation.ThrowIfCancellationRequested();
            memory[year] = entry;
            Save(year, entry);
            return Build(year, entry, ongoing);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or CrsException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            if (cached is not null) return Build(year, cached, ongoing);
            throw new CrsException($"暂时无法取得 {year} 年的官方汇率，也没有可用的同年度缓存。请检查网络后重试。");
        }
    }

    private static bool IsValid(CacheEntry entry, int year)
    {
        var quotes = entry.Quotes;
        if (entry.Version != 1 || entry.From.Year != year || entry.Through.Year != year || entry.From > entry.Through
            || quotes is not { Length: > 0 and <= 366 }
            || !quotes.All(q => q is { Usd: > 0 and <= 1000, Hkd: > 0 and <= 1000 })) return false;
        if (quotes[0].Date < entry.From || quotes[0].Date.DayNumber - entry.From.DayNumber > 14
            || quotes[^1].Date > entry.Through || entry.Through.DayNumber - quotes[^1].Date.DayNumber > 14) return false;
        return quotes.Zip(quotes.Skip(1)).All(p => p.Second.Date.DayNumber - p.First.Date.DayNumber is > 0 and <= 14);
    }

    private CacheEntry? Read(int year)
    {
        if (memory.TryGetValue(year, out var cached)) return cached;
        try
        {
            using var file = File.OpenRead(CachePath(year));
            if (file.Length is <= 0 or > MaxCacheBytes) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(file);
            return entry is not null && IsValid(entry, year) ? entry : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void Save(int year, CacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            AtomicExport.Write(CachePath(year), stream => JsonSerializer.Serialize(stream, entry), CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private string CachePath(int year) => Path.Combine(cacheDirectory, FormattableString.Invariant($"safe-midpoint-v1-{year}.json"));

    private static IExchangeRateProvider Build(int year, CacheEntry entry, bool ongoing)
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
