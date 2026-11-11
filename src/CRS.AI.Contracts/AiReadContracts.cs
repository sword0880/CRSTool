using System.Text.Json;
namespace CRS.AI.Contracts;

// This caller must be constructed by a trusted local host, never from model arguments.
public sealed record AiCaller(string Subject, bool PublicResearchAuthorized);
public sealed record AiCall(string Tool, JsonElement Arguments);
public sealed record AiTool(string Name, string Description, JsonElement Schema);
public sealed record AiResult(string Version, string Tool, string Status, string? Code,
    DateTimeOffset ObservedAt, JsonElement? Data, IReadOnlyList<string> Limitations)
{
    public static AiResult Reject(string tool, string code) =>
        new("1.0", tool, "Rejected", code, DateTimeOffset.UtcNow, null, Array.Empty<string>());
}
public interface IAiReadGateway
{
    IReadOnlyList<AiTool> Tools { get; }
    Task<AiResult> ExecuteAsync(AiCaller caller, AiCall call, CancellationToken token);
}
public static class AiCatalog
{
    public const string Overview = "crs_get_etf_overview_v1";
    public const string Coverage = "crs_get_etf_coverage_v1";
    private static JsonElement Schema(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
    public static IReadOnlyList<AiTool> Tools { get; } = Array.AsReadOnly(new[]
    {
        new AiTool(Overview,"Read the public ETF observation overview. Nulls are unknown; do not infer institutional trading.",
            Schema("""{"type":"object","additionalProperties":false,"properties":{},"required":[]}""")),
        new AiTool(Coverage,"Read registered-calendar ETF data quality, not a fully verified 250-day history.",
            Schema("""
            {"type":"object","additionalProperties":false,
             "properties":{"market":{"type":"string","enum":["SSE","SZSE"]},
             "ticker":{"type":"string","pattern":"^[0-9]{6}$"},
             "from":{"type":"string","format":"date"},"to":{"type":"string","format":"date"}},
             "required":["market","ticker","from","to"]}
            """))
    });
}
public sealed record AiEtfOverviewRow(string Market, string Ticker, string Name, DateOnly? AsOf,
    decimal? Shares, decimal? Change1, decimal? Change5, decimal? Change20, string Status, int CoveredDays);
public sealed record AiCoverageDay(DateOnly Date, string State);
public sealed record AiCoverage(string Market, string Ticker, DateOnly From, DateOnly To,
    int RegisteredTradingDays, int AcceptedDays, int MissingDays, int PendingDays,
    int CriticalDays, int RejectedDays, decimal? CoveragePercent, bool CalendarVerified,
    bool CalendarRangeCovered, string State, IReadOnlyList<AiCoverageDay> Days);
