using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.ExchangeRates;
using CRS.IO;
using CRS.Timing;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using Quartz;

namespace CRS.Worker.Service;

/// <summary>年度汇率唯一持久化入口；SQLCipher 是正式事实源，旧 JSON 只在迁移时读取。</summary>
public sealed class WorkerAnnualExchangeRates : IBackgroundPluginSession
{
    private readonly EncryptedTaskRepository repository;
    private readonly byte[] key;
    private readonly HttpClient? client;
    private readonly string legacyDirectory;
    private readonly TimeProvider time;
    private const int MaxLegacyCacheBytes = 80 * 1024;

    public const string ServiceId = "crs.exchange-rates";
    public const string JobId = "annual-central-parity";
    internal static readonly BackgroundJobDescriptor Descriptor = new(JobId, "年度人民币汇率中间价更新",
        "0 0 9 1 1 ?", BeijingClock.ZoneId, EnabledByDefault: true, RunOnStartup: true);

    public WorkerAnnualExchangeRates(EncryptedTaskRepository repository, string dataDirectory, byte[] key,
        HttpClient? client = null, TimeProvider? clock = null)
    {
        this.repository = repository;
        this.key = key;
        this.client = client;
        legacyDirectory = Path.Combine(dataDirectory, "exchange-rates");
        time = clock ?? TimeProvider.System;
        ImportLegacySeed();
        ImportLegacySnapshots();
    }

    internal bool NeedsCatchUp(TaskDefinition plan)
    {
        var now = BeijingClock.Normalize(time.GetUtcNow());
        var year = now.Year - 1;
        var cron = new CronExpression(plan.Cron, TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId));
        var firstOfYear = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);
        return year >= 2000 && cron.GetNextValidTimeAfter(firstOfYear.AddSeconds(-1)) is { } due && due <= now
            && NeedsUpdate(Read(year), now);
    }

    public async Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = BeijingClock.Normalize(time.GetUtcNow());
        var year = now.Year - 1;
        if (jobId != JobId || year < 2000) return new("Rejected");
        try
        {
            var snapshot = await RefreshAsync(year, false, cancellationToken).ConfigureAwait(false);
            var checkpoint = Checkpoint(year, snapshot);
            return NeedsUpdate(snapshot, now) ? new("NotPublished", checkpoint, TimeSpan.FromDays(1))
                : BackgroundJobResult.Success(checkpoint);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int?)error.StatusCode == 446)
        { return new("AccessBlocked", $"{year} 年官方报价访问受限。"); }
        catch (InvalidDataException) { return new("SourceSchemaChanged", $"{year} 年官方报价校验失败。"); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new("Failed", $"{year} 年末报价更新失败。", TimeSpan.FromHours(6));
        }
    }

    internal BackgroundResponse Query(BackgroundRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TryYear(request, out var year))
            return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest");
        var cached = Read(year);
        return cached is null ? new(WorkerProtocol.Version, request.RequestId, "NotAvailable")
            : new(WorkerProtocol.Version, request.RequestId, "Ok", Payload: JsonSerializer.Serialize(cached));
    }

    internal async Task<BackgroundResponse> RefreshQueryAsync(BackgroundRequest request, CancellationToken token)
    {
        if (!TryYear(request, out var year))
            return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest");
        try
        {
            var snapshot = await RefreshAsync(year, true, token).ConfigureAwait(false);
            return new(WorkerProtocol.Version, request.RequestId, "Ok", Payload: JsonSerializer.Serialize(snapshot));
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int?)error.StatusCode == 446)
        { return new(WorkerProtocol.Version, request.RequestId, "AccessBlocked"); }
        catch (InvalidDataException) { return new(WorkerProtocol.Version, request.RequestId, "SourceSchemaChanged"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        { return new(WorkerProtocol.Version, request.RequestId, "NotAvailable"); }
    }

    internal BackgroundResponse LegacySnapshotQuery(BackgroundRequest request, string ownerSid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TryYear(request, out var year))
            return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest");
        var snapshot = Read(year, "DesktopLegacy", ownerSid);
        return snapshot is null ? new(WorkerProtocol.Version, request.RequestId, "NotAvailable")
            : new(WorkerProtocol.Version, request.RequestId, "Ok", Payload: JsonSerializer.Serialize(snapshot));
    }

    internal BackgroundResponse LegacyQuery(BackgroundRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TryYear(request, out var year))
            return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest");
        return new(WorkerProtocol.Version, request.RequestId, "Ok",
            Payload: JsonSerializer.Serialize(repository.ReadLegacyAnnualRates(year)));
    }

    internal BackgroundResponse ImportDesktopSnapshot(BackgroundRequest request, string ownerSid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.RequestId == Guid.Empty || request.Payload is not { Length: > 0 and <= 80000 })
            return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest");
        try
        {
            var snapshot = JsonSerializer.Deserialize<CentralParitySnapshot>(request.Payload);
            if (snapshot is null || !TryYear(snapshot, out var year) || !ValidWindow(year, snapshot))
                return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest");
            repository.SaveAnnualExchangeRateSnapshot(year, snapshot, "DesktopLegacy", ownerSid);
            return new(WorkerProtocol.Version, request.RequestId, "Ok");
        }
        catch (JsonException) { return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest"); }
        catch (InvalidDataException) { return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest"); }
    }

    private async Task<CentralParitySnapshot> RefreshAsync(int year, bool force, CancellationToken token)
    {
        var now = BeijingClock.Normalize(time.GetUtcNow());
        var today = DateOnly.FromDateTime(now.Date);
        if (year is < 2000 || year > today.Year) throw new InvalidDataException("汇率年度超出允许范围。");
        var ongoing = year == today.Year;
        var (from, through) = ongoing
            ? (new DateOnly(year, 1, 1), today)
            : CentralParitySnapshot.YearEndRange(year);
        var cached = Read(year);
        var eligible = cached is not null && cached.From == from && cached.Through <= through &&
            (ongoing || cached.IsYearEnd(year)) ? cached : null;
        if (!force && eligible is not null &&
            ((!ongoing && eligible.Quotes[^1].Date == through) ||
             (ongoing && eligible.Through == through && eligible.FetchedAt <= now && now - eligible.FetchedAt < TimeSpan.FromHours(1))))
            return eligible;

        try
        {
            var downloaded = await SafeCentralParityFeed.ReadAsync(from, through, now, token, client).ConfigureAwait(false);
            if (!ValidWindow(year, downloaded)) throw new InvalidDataException("官方报价缺失或区间不完整。");
            token.ThrowIfCancellationRequested();
            // 数据库按 (年度、来源、报价日) 首次写入；重复日期即使被官方修订也不覆盖。
            return repository.SaveAnnualExchangeRateSnapshot(year, downloaded);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            if (eligible is not null) return eligible;
            throw;
        }
    }

    private bool ValidWindow(int year, CentralParitySnapshot snapshot)
    {
        var today = DateOnly.FromDateTime(BeijingClock.Normalize(time.GetUtcNow()).Date);
        if (!snapshot.IsValid(year) || year > today.Year) return false;
        if (year < today.Year) return snapshot.IsYearEnd(year);
        return snapshot.From == new DateOnly(year, 1, 1) && snapshot.Through <= today;
    }

    private static bool TryYear(BackgroundRequest request, out int year)
    {
        year = 0;
        return request.RequestId != Guid.Empty &&
            request == new BackgroundRequest(WorkerProtocol.Version, request.RequestId, request.Command, Payload: request.Payload) &&
            request.Payload is { Length: 4 } raw &&
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out year) &&
            year is >= 2000 and <= 2100;
    }

    private static bool TryYear(CentralParitySnapshot snapshot, out int year)
    {
        year = snapshot.Through.Year;
        return year is >= 2000 and <= 2100 && snapshot.From.Year == year;
    }

    private CentralParitySnapshot? Read(int year, string origin = "Official", string ownerSid = "") =>
        repository.ReadAnnualExchangeRateSnapshot(year, origin, ownerSid);

    private static bool NeedsUpdate(CentralParitySnapshot? snapshot, DateTimeOffset now) => snapshot is null
        || snapshot.Quotes[^1].Date != snapshot.Through && now.Month == 1 && now.Day <= 7;

    private static string Checkpoint(int year, CentralParitySnapshot snapshot) => FormattableString.Invariant(
        $"{year} 年报价日 {snapshot.Quotes[^1].Date:yyyy-MM-dd}{(snapshot.Quotes[^1].Date == snapshot.Through ? "（年度末日）" : "（该年最后报价，临时测算）")}。");

    private void ImportLegacySeed()
    {
        const string resource = "CRS.Worker.Service.Resources.legacy-annual-exchange-rates.json";
        using var stream = typeof(WorkerAnnualExchangeRates).Assembly.GetManifestResourceStream(resource);
        if (stream is null) throw new InvalidDataException("年度汇率迁移种子缺失。");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (buffer.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("年度汇率迁移种子超限。");
        var bytes = buffer.ToArray();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("年度汇率迁移种子版本不支持。");
            var rates = document.RootElement.GetProperty("rates").Deserialize<LegacyAnnualExchangeRateRecord[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            repository.ImportLegacyAnnualRates(rates, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void ImportLegacySnapshots()
    {
        if (!Directory.Exists(legacyDirectory)) return;
        var info = new DirectoryInfo(legacyDirectory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("旧汇率目录不能是链接。");
        foreach (var path in Directory.EnumerateFiles(legacyDirectory, "safe-midpoint-v1-*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith("safe-midpoint-v1-", StringComparison.Ordinal) ||
                !int.TryParse(name["safe-midpoint-v1-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
                year is < 2000 or > 2100 || Read(year) is not null) continue;
            try
            {
                var file = new FileInfo(path);
                if (file.Length is <= 0 or > MaxLegacyCacheBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var snapshot = AuthenticatedJsonFile.Read<CentralParitySnapshot>(path, key, Purpose(year));
                if (snapshot is { } valid && valid.IsYearEnd(year))
                    repository.SaveAnnualExchangeRateSnapshot(year, valid);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException)
            { /* 无法验证的旧文件保留为隔离资料，不导入数据库。 */ }
        }
    }

    private static string Purpose(int year) => FormattableString.Invariant($"annual-exchange-rates/{year}");

    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs() => [Descriptor];
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
