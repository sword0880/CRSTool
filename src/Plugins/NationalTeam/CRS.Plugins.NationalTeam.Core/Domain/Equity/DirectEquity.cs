namespace CRS.Plugins.NationalTeamWatch.Domain;

public sealed record EquityWatch(string Market,string Ticker,string Name,bool Active,bool Favorite);
public sealed record EquityReportDocument(string Id,string Market,string Ticker,string BulletinId,string Title,DateOnly Announced,string DocumentUrl,string State,string ListSourceUrl);
public sealed record EquityReportPage(int Total,int Pages,IReadOnlyList<string> GroupIds,IReadOnlyList<EquityReportDocument> Documents);
public sealed record EquityReportSearch(Guid RequestId,string State,string Code);
public sealed record EquityHolder(string Account,decimal Shares,decimal? Percent);
public sealed record EquityDisclosure(string Id,string Market,string Ticker,DateOnly SourceDate,DateOnly? ReportAsOf,DateOnly? Published,
    string State,string SourceUrl,string Hash,string? DocumentUrl,string? Page,string? Reviewer,bool Comparable,string Nature,IReadOnlyList<EquityHolder> Holders);
public sealed record EquityHoldingRow(string DisclosureId,string Market,string Ticker,string Name,string Institution,string Account,
    decimal? Shares,decimal? Percent,DateOnly? ReportAsOf,DateOnly? Published,DateOnly SourceDate,string State,decimal? Delta,string Nature,bool Favorite,string SourceUrl,DateOnly? ComparisonFrom=null,string? EvidencePage=null,string? EvidenceHash=null)
{
    public string StatusText=>State switch{"Increased"=>"报告期持股数增加","Decreased"=>"报告期持股数减少","Unchanged"=>"报告期持股数持平","FirstListed"=>"首次列示，前值未知","NotListed"=>"本期未列示","NotComparable"=>"口径不可比","NeedsReview"=>"待核验原文与报告期","Critical"=>"证据异常","Conflict"=>"同日来源冲突",_=>State};
    public EvidenceLevel EvidenceLevel=>(State is "Increased" or "Decreased" or "Unchanged" or "FirstListed" or "NotComparable")
        && ReportAsOf.HasValue && Published.HasValue && Shares.HasValue && !string.IsNullOrWhiteSpace(EvidencePage)
        && EvidenceHash is {Length:64} && EvidenceHash.All(Uri.IsHexDigit) ? EvidenceLevel.DisclosedHolding : EvidenceLevel.ResearchHypothesis;
    public string EvidenceText=>EvidenceSemantics.Text(EvidenceLevel);
    public HoldingRelationship Relationship=>HoldingRelationship.DirectEquityHolding;
    public string Basis=>EvidenceSemantics.SnapshotBasis;
    public string NatureText=>Nature=="Strategic"?"战略控股/参股":Nature=="Portfolio"?"证券投资持仓":"性质待核验";
}
public sealed record EquityReview(string Id,DateOnly ReportAsOf,DateOnly Published,string DocumentUrl,string Page,string Reviewer,bool Comparable,string Nature,string ShareClass="A",string PercentBasis="TotalShares",string Account="",bool OriginalConfirmed=false,string? Institution=null)
{
    /// <summary>审核界面实际显示的前期披露；后台不得把过期确认自动套用到新基线。</summary>
    public string? ComparisonDisclosureId {get;init;}
}

public static class DirectEquityComparison
{
    public static (string State,decimal? Delta) Compare(decimal? previous,decimal? current,bool comparable,bool previousKnown)
        =>current is null?("NotListed",null):!previousKnown || previous is null?("FirstListed",null):!comparable?("NotComparable",null)
            :current>previous?("Increased",current-previous):current<previous?("Decreased",current-previous):("Unchanged",0m);
}
