namespace CRS.Plugins.NationalTeamWatch.Domain;
public sealed record EtfKey(string Market, string Ticker);
public sealed record SharePoint(DateOnly Date, decimal Shares, decimal? Nav, string RevisionId);
public sealed record Metric(decimal? DeltaShares, decimal? EstimatedFlow, string State, string Fingerprint)
{
    public EvidenceLevel EvidenceLevel=>EvidenceLevel.MarketObservation;
    public string Basis=>EvidenceSemantics.EtfBasis;
}
/// <summary>份额变化与可选同日NAV估算；不用于归因某个机构的真实交易。</summary>
public static class EtfMetrics
{
    public const string AlgorithmVersion = "shares-v1";
    public static decimal Normalize(decimal value, string unit) => value < 0 ? throw new InvalidDataException("Negative shares.") : unit switch
    { "份" => value, "万份" => checked(value * 10000m), _ => throw new InvalidDataException("Unknown shares unit.") };
    // 使用人工核对的交易所日历；既不按工作日推算，也不跳过缺失的相邻交易日。
    public static Metric Calculate(DateOnly date, int window, IReadOnlyList<DateOnly> tradingDays, IReadOnlyList<SharePoint> points)
    {
        if (window is not (1 or 5 or 20)) throw new ArgumentOutOfRangeException(nameof(window));
        var days = tradingDays.Distinct().Order().ToArray(); var index = Array.IndexOf(days, date);
        if (index < window) return new(null, null, "Incomplete", "");
        var selected = days[(index - window)..(index + 1)]; var rows = new List<SharePoint>();
        foreach (var day in selected)
        { var row = points.SingleOrDefault(x => x.Date == day); if (row is null) return new(null, null, "Incomplete", ""); rows.Add(row); }
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            AlgorithmVersion + "|" + string.Join('|', rows.Select(x => $"{x.Date:yyyy-MM-dd}:{x.RevisionId}:{x.Nav?.ToString(System.Globalization.CultureInfo.InvariantCulture)}")))));
        var delta = rows[^1].Shares - rows[0].Shares;
        decimal? flow = 0;
        for (var i = 1; i < rows.Count; i++)
        { if (rows[i].Nav is null) { flow = null; break; } flow += (rows[i].Shares - rows[i-1].Shares) * rows[i].Nav; }
        return new(delta, flow, flow is null ? "NAV Unavailable" : "Complete", fingerprint);
    }
}
