using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.AI.Contracts;
using CRS.Worker.Ipc;

namespace CRS.AI.Query;

/// <summary>
/// 公开 ETF 只读查询，校验参数并将 Worker 响应投影为允许公开的字段。
/// </summary>
internal sealed class AiReadQueryService(IWorkerPublicReadClient worker) : IAiReadGateway
{
    private readonly WorkerEtfSnapshotReader _reader = new(worker);
    private static readonly JsonSerializerOptions Strict = new()
    {
        MaxDepth = 32,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private static readonly string[] Warnings =
    [
        "ETF份额变化不可直接归因为国家队或其他机构的买卖。",
        "已登记日历的覆盖率不等于250日历史及连续采集已通过金融验收。"
    ];
    public IReadOnlyList<AiTool> Tools => AiCatalog.Tools;

    public async Task<AiResult> ExecuteAsync(AiCaller caller, AiCall call, CancellationToken token)
    {
        if (caller is null || string.IsNullOrWhiteSpace(caller.Subject) || !caller.PublicResearchAuthorized)
            return AiResult.Reject(call.Tool, "Unauthorized");
        if (call.Tool is not (AiCatalog.Overview or AiCatalog.Coverage))
            return AiResult.Reject(call.Tool, "UnknownTool");
        if (!Validate(call, out var market, out var ticker, out var from, out var to))
            return AiResult.Reject(call.Tool, "InvalidArguments");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var operation = call.Tool == AiCatalog.Overview ? "overview" : "coverage";
            var snapshot = await _reader.ReadAsync(operation, market, ticker, from, to, timeout.Token);
            if (operation == "overview")
            {
                var summary = snapshot.Rows.Select(Deserialize<AiEtfOverviewRow>).ToArray();
                return Result(call.Tool, "Partial", "FinancialAcceptanceNotEstablished",
                    JsonSerializer.SerializeToElement(summary));
            }
            if (snapshot.Metadata is null) return AiResult.Reject(call.Tool, "UpstreamInvalid");
            var meta = Deserialize<CoverageMetadata>(snapshot.Metadata.Value);
            if (meta.Market != market || meta.Ticker != ticker || meta.From != from || meta.To != to)
                return AiResult.Reject(call.Tool, "UpstreamInvalid");
            var days = snapshot.Rows.Select(Deserialize<AiCoverageDay>).ToArray();
            var data = new AiCoverage(meta.Market, meta.Ticker, meta.From, meta.To, meta.RegisteredTradingDays,
                meta.AcceptedDays, meta.MissingDays, meta.PendingDays, meta.CriticalDays,
                meta.RejectedDays, meta.CoveragePercent, meta.CalendarVerified,
                meta.CalendarRangeCovered, meta.State, days);
            var status = meta.CalendarVerified && meta.CalendarRangeCovered &&
                         meta.State == "CompleteRegisteredCalendar"
                ? "Partial" : "NotReady";
            return Result(call.Tool, status, status == "NotReady" ? "CoverageNotVerified" : "RegisteredCalendarOnly",
                JsonSerializer.SerializeToElement(data));
        }
        catch (OperationCanceledException)
        {
            return AiResult.Reject(call.Tool, token.IsCancellationRequested ? "Cancelled" : "UpstreamTimeout");
        }
        catch (WorkerQueryRejectedException ex)
        {
            return AiResult.Reject(call.Tool, ex.Code switch
            {
                "Forbidden" or "AccessDenied" => "Unauthorized",
                "UnknownQuery" or "ProtocolMismatch" => "Unsupported",
                "PluginBusy" or "RateLimited" or "Busy" => "BudgetExceeded",
                "Maintenance" or "StorageFault" or "PluginUnresponsive" => "UpstreamUnavailable",
                "RequestTimedOut" => "UpstreamTimeout",
                _ => "UpstreamFailure"
            });
        }
        catch (BackgroundServiceException ex)
        {
            return AiResult.Reject(call.Tool, ex.Code switch
            {
                "AccessDenied" => "Unauthorized",
                "ServiceUnavailable" or "ConnectionFailed" or "ResponseInterrupted" or "ServiceIdentityUnavailable" =>
                    "UpstreamUnavailable",
                "ResponseTimedOut" or "RequestTimedOut" => "UpstreamTimeout",
                _ => "UpstreamFailure"
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or
                                   ArgumentException or OverflowException)
        {
            return AiResult.Reject(call.Tool, "UpstreamInvalid");
        }
    }

    private static AiResult Result(string tool, string status, string reason, JsonElement data)
        => new("1.0", tool, status, reason, DateTimeOffset.UtcNow, data, Warnings)
        {
            // 只用脱敏后的公开数据计算指纹，查询时间和临时游标不会改变版本。
            DataVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.GetRawText())))
        };

    private static bool Validate(AiCall call, out string? market, out string? ticker,
        out DateOnly? from, out DateOnly? to)
    {
        market = ticker = null; from = to = null;
        if (call.Arguments.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in call.Arguments.EnumerateObject())
            if (!names.Add(property.Name)) return false;
        if (call.Tool == AiCatalog.Overview) return names.Count == 0;
        if (!names.SetEquals(["market", "ticker", "from", "to"])) return false;
        foreach (var name in names)
            if (call.Arguments.GetProperty(name).ValueKind != JsonValueKind.String) return false;
        market = call.Arguments.GetProperty("market").GetString();
        ticker = call.Arguments.GetProperty("ticker").GetString();
        if (market is not ("SSE" or "SZSE") || ticker is null || ticker.Length != 6 ||
            ticker.Any(c => c is < '0' or > '9')) return false;
        var textFrom = call.Arguments.GetProperty("from").GetString();
        var textTo = call.Arguments.GetProperty("to").GetString();
        if (!DateOnly.TryParseExact(textFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var f) ||
            !DateOnly.TryParseExact(textTo, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t) ||
            t < f || t.DayNumber - f.DayNumber > 30) return false;
        from = f; to = t;
        return true;
    }

    private static T Deserialize<T>(JsonElement element)
        => element.Deserialize<T>(Strict) ?? throw new InvalidDataException("Invalid DTO.");
    private sealed record CoverageMetadata(string Market, string Ticker, DateOnly From, DateOnly To,
        int RegisteredTradingDays, int AcceptedDays, int MissingDays, int PendingDays,
        int CriticalDays, int RejectedDays, decimal? CoveragePercent, bool CalendarVerified,
        bool CalendarRangeCovered, string State);
}
