using System.Text;
using System.Text.Json;

namespace CRS.AI.Query;

/// <summary>按 Worker V1 协议读取有界快照，拒绝混页、漏页和提前结束。</summary>
internal sealed class WorkerEtfSnapshotReader(IWorkerPublicReadClient worker)
{
    private const int MaxRows = 64;
    private const int MaxPages = 8;
    private const int MaxBytes = 48 * 1024;
    private static readonly JsonSerializerOptions Strict = new()
    {
        MaxDepth = 32,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public async Task<WorkerEtfSnapshot> ReadAsync(string operation, string? market, string? ticker,
        DateOnly? from, DateOnly? to, CancellationToken token)
    {
        var rows = new List<JsonElement>();
        string? metadata = null;
        string? snapshot = null;
        int? totalRows = null;
        var bytesRead = 0;
        // 首页使用历史 V1 支持的页大小，服务声明能力后才增大后续页。
        var count = 10;
        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var offset = rows.Count;
            var payload = JsonSerializer.Serialize(new
            {
                Operation = operation, Market = market, Ticker = ticker, From = from, To = to,
                Offset = offset, Count = count, Snapshot = snapshot, ContractVersion = 1
            });
            var response = await worker.QueryAsync(operation, payload, token);
            if (response.Code != "Ok") throw new WorkerQueryRejectedException(response.Code);
            if (response.Payload is null) throw new InvalidDataException("Worker result is empty.");
            bytesRead += Encoding.UTF8.GetByteCount(response.Payload);
            if (bytesRead > MaxBytes) throw new InvalidDataException("Snapshot exceeds byte limit.");

            var page = ParseChecked(response.Payload).Deserialize<Page>(Strict)
                ?? throw new InvalidDataException("Invalid page.");
            if (page.ContractVersion != 1 || string.IsNullOrWhiteSpace(page.Snapshot) ||
                page.Snapshot.Length > 128 || (snapshot is not null && snapshot != page.Snapshot) ||
                page.MaximumPageCount is < 1 or > 256)
                throw new InvalidDataException("Invalid snapshot.");
            snapshot = page.Snapshot;

            var items = ParseChecked(page.Items);
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > count ||
                offset + items.GetArrayLength() > MaxRows)
                throw new InvalidDataException("Invalid page items.");
            foreach (var row in items.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid page row.");
                rows.Add(row);
            }

            // 旧服务可以省略总行数；一旦声明，同一快照的总数必须保持一致。
            if (page.TotalRows is { } reportedTotal)
            {
                if (reportedTotal is < 0 or > MaxRows || (totalRows.HasValue && totalRows != reportedTotal))
                    throw new InvalidDataException("Invalid total row count.");
                totalRows = reportedTotal;
            }
            if (page.Metadata is not null)
            {
                if (metadata is not null && !string.Equals(metadata, page.Metadata, StringComparison.Ordinal))
                    throw new InvalidDataException("Metadata drift.");
                metadata = page.Metadata;
            }

            if (page.NextOffset is null)
            {
                if (totalRows.HasValue && rows.Count != totalRows)
                    throw new InvalidDataException("Snapshot ended before its declared row count.");
                return new(rows, metadata is null ? null : ParseChecked(metadata));
            }
            if (page.NextOffset <= offset || page.NextOffset != rows.Count || rows.Count >= MaxRows ||
                (totalRows.HasValue && rows.Count >= totalRows))
                throw new InvalidDataException("Invalid cursor.");

            count = Math.Min(page.MaximumPageCount ?? count, MaxRows - rows.Count);
        }
        throw new InvalidDataException("Too many pages.");
    }

    private static JsonElement ParseChecked(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(document.RootElement);
        return document.RootElement.Clone();
    }

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

    private sealed record Page(string Items, string Snapshot, int? NextOffset,
        string? Metadata = null, int ContractVersion = 1, int? TotalRows = null, int? MaximumPageCount = null);
}

internal sealed record WorkerEtfSnapshot(IReadOnlyList<JsonElement> Rows, JsonElement? Metadata);

internal sealed class WorkerQueryRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
