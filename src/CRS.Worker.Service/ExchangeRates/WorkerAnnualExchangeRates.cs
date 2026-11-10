using System.Globalization;
using System.Net;
using System.Text.Json;
using CRS.ExchangeRates;
using CRS.IO;
using CRS.Timing;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using Quartz;

namespace CRS.Worker.Service;

/// <summary>服务拥有年度报价的更新和持久化，只通过管道公开校验后的报价。</summary>
public sealed class WorkerAnnualExchangeRates(string dataDirectory, byte[] key, HttpClient? client = null,
    TimeProvider? clock = null) : IBackgroundPluginSession
{
    public const string ServiceId = "crs.exchange-rates";
    public const string JobId = "annual-central-parity";
    internal static readonly BackgroundJobDescriptor Descriptor = new(JobId, "年度人民币汇率中间价更新",
        "0 0 9 1 1 ?", BeijingClock.ZoneId, EnabledByDefault: true, RunOnStartup: true);
    private readonly string directory = Path.Combine(dataDirectory, "exchange-rates");
    private readonly TimeProvider time = clock ?? TimeProvider.System;

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
        var cached = Read(year);
        if (cached is not null && !NeedsUpdate(cached, now)) return BackgroundJobResult.Success(Checkpoint(year, cached));

        var (from, through) = CentralParitySnapshot.YearEndRange(year);
        try
        {
            var snapshot = await SafeCentralParityFeed.ReadAsync(from, through, now, cancellationToken, client).ConfigureAwait(false);
            if (!snapshot.IsYearEnd(year)) return new("NotPublished", $"{year} 年末暂无有效报价。", TimeSpan.FromHours(6));
            cancellationToken.ThrowIfCancellationRequested();
            if (cached is not null && cached.Quotes[^1].Date > snapshot.Quotes[^1].Date) snapshot = cached;
            Directory.CreateDirectory(PhysicalPathGuard.RejectLinks(directory));
            AuthenticatedJsonFile.Write(PathFor(year), key, Purpose(year), snapshot);
            var checkpoint = Checkpoint(year, snapshot);
            return NeedsUpdate(snapshot, now) ? new("NotPublished", checkpoint, TimeSpan.FromDays(1))
                : BackgroundJobResult.Success(checkpoint);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int?)error.StatusCode == 446)
        { return new("AccessBlocked", $"{year} 年官方报价访问受限。"); }
        catch (InvalidDataException) { return new("SourceSchemaChanged", $"{year} 年官方报价校验失败。"); }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new("Failed", $"{year} 年末报价更新失败。", TimeSpan.FromHours(6));
        }
    }

    internal BackgroundResponse Query(BackgroundRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.RequestId == Guid.Empty || request != new BackgroundRequest(WorkerProtocol.Version, request.RequestId, WorkerProtocol.GetAnnualExchangeRates, Payload: request.Payload)
            || request.Payload is not { Length: 4 } raw || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || year < 2000 || year >= BeijingClock.Normalize(time.GetUtcNow()).Year)
            return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest");
        var cached = Read(year);
        return cached is null ? new(WorkerProtocol.Version, request.RequestId, "NotAvailable")
            : new(WorkerProtocol.Version, request.RequestId, "Ok", Payload: JsonSerializer.Serialize(cached));
    }

    private CentralParitySnapshot? Read(int year)
    {
        try
        {
            var cached = AuthenticatedJsonFile.Read<CentralParitySnapshot>(PathFor(year), key, Purpose(year));
            return cached.IsYearEnd(year) ? cached : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return null; }
    }

    private static bool NeedsUpdate(CentralParitySnapshot? snapshot, DateTimeOffset now) => snapshot is null
        || snapshot.Quotes[^1].Date != snapshot.Through && now.Month == 1 && now.Day <= 7;

    private static string Checkpoint(int year, CentralParitySnapshot snapshot) => FormattableString.Invariant(
        $"{year} 年报价日 {snapshot.Quotes[^1].Date:yyyy-MM-dd}{(snapshot.Quotes[^1].Date == snapshot.Through ? "（年度末日）" : "（该年最后报价，临时测算）")}。");

    private string PathFor(int year) => Path.Combine(directory, FormattableString.Invariant($"safe-midpoint-v1-{year}.json"));
    private static string Purpose(int year) => FormattableString.Invariant($"annual-exchange-rates/{year}");

    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs() => [Descriptor];
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
