using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CRS.Worker.Ipc;

/// <summary>Worker 在分派之前校验固定插件、操作和分页载荷。</summary>
public static class AiGatewayWorkerRequestPolicy
{
    public const string PluginId = "crs.national-team";
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "Operation", "Market", "Ticker", "From", "To", "Offset", "Count", "Snapshot", "ContractVersion"
    };

    internal static bool Allows(BackgroundRequest request) =>
        request.Command == "QueryPlugin" && request.PluginId == PluginId &&
        request.JobId is null && request.Cron is null && request.TimeZoneId is null &&
        request.ExpectedRevision is null && request.Cursor is null && request.PageSize == 10 &&
        request.RelatedRequestId is null && request.ExecutionId is null && request.ResultFilter is null &&
        ValidatePayload(request.Payload, expectedOperation: null, singlePage: true);

    public static bool MatchesPayload(string operation, string? payload) =>
        (operation is "overview" or "coverage") && ValidatePayload(payload, operation);

    public static bool IsSinglePageRoute(string pluginId, string payload)
    {
        if (pluginId != PluginId) return false;
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        return document.RootElement.ValueKind == JsonValueKind.Object &&
            StringValue(document.RootElement, "Operation") is "ai-public-overview" or "ai-public-coverage";
    }

    internal static BackgroundRequest UseSinglePageRoute(BackgroundRequest request)
    {
        // 仅对已经通过校验的专用管道请求改写路由。客户端无法指定内部操作或桌面快照。
        var payload = JsonNode.Parse(request.Payload!)!.AsObject();
        payload["Operation"] = "ai-public-" + payload["Operation"]!.GetValue<string>();
        return request with { Payload = payload.ToJsonString() };
    }

    private static bool ValidatePayload(string? payload, string? expectedOperation, bool singlePage = false)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > 4096 ||
            Encoding.UTF8.GetByteCount(payload) > 4096)
            return false;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name) || !seen.Add(property.Name)) return false;

            var operation = StringValue(root, "Operation");
            if ((expectedOperation is not null && operation != expectedOperation) ||
                !TryReadInt(root, "ContractVersion", out var version) || version != 1 ||
                !TryReadInt(root, "Offset", out var offset) || offset is < 0 or >= 64 || (singlePage && offset != 0) ||
                !TryReadInt(root, "Count", out var count) || count < 1 || count > 64 - offset ||
                !IsValidSnapshot(root, offset))
                return false;

            return operation switch
            {
                "overview" => IsAbsent(root, "Market") && IsAbsent(root, "Ticker") &&
                              IsAbsent(root, "From") && IsAbsent(root, "To"),
                "coverage" => IsValidCoverage(root),
                _ => false
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsValidSnapshot(JsonElement root, int offset)
    {
        // 第一页建立快照。续页必须携带由 Worker 签发的非空快照标识。
        if (offset == 0) return IsAbsent(root, "Snapshot");
        var snapshot = StringValue(root, "Snapshot");
        return !string.IsNullOrWhiteSpace(snapshot) && snapshot.Length <= 128;
    }

    private static bool IsValidCoverage(JsonElement root) =>
        StringValue(root, "Market") is "SSE" or "SZSE" &&
        StringValue(root, "Ticker") is { Length: 6 } ticker &&
        ticker.All(character => character is >= '0' and <= '9') &&
        DateOnly.TryParseExact(StringValue(root, "From"), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) &&
        DateOnly.TryParseExact(StringValue(root, "To"), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) &&
        to.DayNumber - from.DayNumber is >= 0 and <= 30;

    private static bool TryReadInt(JsonElement root, string field, out int value)
    {
        value = default;
        return root.TryGetProperty(field, out var node) &&
            node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out value);
    }

    private static string? StringValue(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool IsAbsent(JsonElement root, string field) =>
        !root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null;
}
