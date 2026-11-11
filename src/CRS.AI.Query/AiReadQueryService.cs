using System.Globalization;
using System.Text;
using System.Text.Json;
using CRS.AI.Contracts;
using CRS.Worker.Ipc;

namespace CRS.AI.Query;

/// <summary>
/// A strictly bounded, offline-testable read facade. Caller identity is asserted by the
/// trusted local host; an external MCP/HTTP server MUST implement separate authentication.
/// </summary>
internal sealed class AiReadQueryService(IWorkerPublicReadClient worker) : IAiReadGateway
{
    private const int MaxRows = 64, MaxPages = 8, MaxBytes = 48 * 1024;
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
            var snapshot = await ReadAsync(operation, market, ticker, from, to, timeout.Token);
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
        => new("1.0", tool, status, reason, DateTimeOffset.UtcNow, data, Warnings);

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

    private async Task<Snapshot> ReadAsync(string operation, string? market, string? ticker,
        DateOnly? from, DateOnly? to, CancellationToken token)
    {
        var rows = new List<JsonElement>();
        JsonElement? metadata = null;
        string? snapshot = null;
        int offset = 0, byteCount = 0;
        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            // Request field names and business V1 match current EtfRequest exactly.
            var payload = JsonSerializer.Serialize(new
            {
                Operation = operation, Market = market, Ticker = ticker, From = from, To = to,
                Offset = offset, Count = MaxRows, Snapshot = snapshot, ContractVersion = 1
            });
            var response = await worker.QueryAsync(operation, payload, token);
            if (response.Code != "Ok") throw new WorkerQueryRejectedException(response.Code);
            if (response.Payload is null) throw new InvalidDataException("Worker result is empty.");
            byteCount += Encoding.UTF8.GetByteCount(response.Payload);
            if (byteCount > MaxBytes) throw new InvalidDataException("Too much data.");
            using var document = JsonDocument.Parse(response.Payload, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicates(document.RootElement);
            var page = Deserialize<Page>(document.RootElement);
            if (page.ContractVersion != 1 || string.IsNullOrEmpty(page.Snapshot) ||
                page.Snapshot.Length > 128 || snapshot is not null && snapshot != page.Snapshot ||
                page.TotalRows is > MaxRows)
                throw new InvalidDataException("Invalid snapshot.");
            snapshot = page.Snapshot;
            using var items = JsonDocument.Parse(page.Items, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicates(items.RootElement);
            if (items.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid page items.");
            foreach (var row in items.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || rows.Count >= MaxRows)
                    throw new InvalidDataException("Page row limit.");
                rows.Add(row.Clone());
            }
            if (page.Metadata is not null)
            {
                using var meta = JsonDocument.Parse(page.Metadata, new JsonDocumentOptions { MaxDepth = 32 });
                RejectDuplicates(meta.RootElement);
                if (metadata is not null && metadata.Value.GetRawText() != meta.RootElement.GetRawText())
                    throw new InvalidDataException("Metadata drift.");
                metadata = meta.RootElement.Clone();
            }
            if (page.NextOffset is null) return new(rows, metadata);
            if (page.NextOffset <= offset || page.NextOffset != rows.Count)
                throw new InvalidDataException("Invalid cursor.");
            offset = page.NextOffset.Value;
        }
        throw new InvalidDataException("Too many pages.");
    }
    private static T Deserialize<T>(JsonElement element)
        => element.Deserialize<T>(Strict) ?? throw new InvalidDataException("Invalid DTO.");
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate field.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var row in element.EnumerateArray()) RejectDuplicates(row);
    }
    private sealed class WorkerQueryRejectedException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
    private sealed record Snapshot(IReadOnlyList<JsonElement> Rows, JsonElement? Metadata);
    private sealed record Page(string Items, string Snapshot, int? NextOffset,
        string? Metadata = null, int ContractVersion = 1, int? TotalRows = null);
    private sealed record CoverageMetadata(string Market, string Ticker, DateOnly From, DateOnly To,
        int RegisteredTradingDays, int AcceptedDays, int MissingDays, int PendingDays,
        int CriticalDays, int RejectedDays, decimal? CoveragePercent, bool CalendarVerified,
        bool CalendarRangeCovered, string State);
}
