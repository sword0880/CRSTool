namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>同一市场及代码只占一行；保留具体账户与各自报告期，不合计股数。</summary>
public sealed record EquityStockSummary(string Market,string Ticker,string Name,IReadOnlyList<EquityHoldingRow> Holders)
{
    public int AccountCount=>Holders.Select(x=>(x.Institution,x.Account)).Distinct().Count();
    public int InstitutionCount=>Holders.Select(x=>x.Institution).Distinct().Count();
    public string InstitutionsText=>string.Join("、",Holders.Select(x=>x.Institution).Distinct());
    public DateOnly? ReportFrom=>Holders.Min(x=>x.ReportAsOf);
    public DateOnly? ReportUntil=>Holders.Max(x=>x.ReportAsOf);
    public string ReportPeriodText=>ReportFrom is not {} from?"待核验":(ReportUntil==from?$"{from:yyyy-MM-dd}":$"{from:yyyy-MM-dd} 至 {ReportUntil:yyyy-MM-dd}")
        +(Holders.Any(x=>x.ReportAsOf is null)?" / 部分待核验":"");
    public string EvidenceStatus=>Holders.All(EquityStockList.IsVerifiedHolding)?"已核验披露快照":string.Join(" / ",Holders.Select(x=>x.StatusText).Distinct());
}

public static class EquityStockList
{
    // 正式清单仅接受证据完整、主体已确认且股数为正的快照；未列示和零值不推断清仓。
    public static bool IsVerifiedHolding(EquityHoldingRow row)=>row.EvidenceLevel==EvidenceLevel.DisclosedHolding
        &&InstitutionAccounts.IsSupportedGroup(row.Institution)&&row.Shares>0;

    public static IReadOnlyList<EquityStockSummary> Build(IEnumerable<EquityHoldingRow> rows,bool verified)
        =>rows.Where(x=>IsVerifiedHolding(x)==verified).GroupBy(x=>(x.Market,x.Ticker))
            .Select(g=>new EquityStockSummary(g.Key.Market,g.Key.Ticker,g.First().Name,
                g.OrderBy(x=>x.Institution,StringComparer.Ordinal).ThenBy(x=>x.Account,StringComparer.Ordinal)
                    .ThenByDescending(x=>x.ReportAsOf).ThenByDescending(x=>x.SourceDate).ToArray()))
            .OrderByDescending(x=>x.Holders.Any(h=>h.Favorite)).ThenBy(x=>x.Market,StringComparer.Ordinal).ThenBy(x=>x.Ticker,StringComparer.Ordinal).ToArray();
}
