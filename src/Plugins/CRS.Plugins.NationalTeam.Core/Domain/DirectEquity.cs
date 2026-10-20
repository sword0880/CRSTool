using System.Text.RegularExpressions;
namespace CRS.Plugins.NationalTeamWatch.Domain;

public static class InstitutionAccounts
{
    public static string? CandidateGroup(string account)=>Group(account)??(account.Trim() is "汇金公司" or "社保基金会" || account.Contains("社保基金",StringComparison.Ordinal)||account.Contains("基本养老保险基金",StringComparison.Ordinal)?"主体待核验":null);
    public static string? Group(string account)=>account.Trim() switch
    {
        "中央汇金投资有限责任公司"=>"汇金投资",
        "中央汇金资产管理有限责任公司"=>"汇金资管",
        "中国证券金融股份有限公司"=>"证金公司",
        "全国社会保障基金理事会"=>"社保基金",
        var name when Regex.IsMatch(name,@"^全国社保基金[〇零一二三四五六七八九十百0-9]+组合$",RegexOptions.None,TimeSpan.FromMilliseconds(100))=>"社保基金",
        var name when Regex.IsMatch(name,@"^基本养老保险基金[〇零一二三四五六七八九十百0-9]+组合$",RegexOptions.None,TimeSpan.FromMilliseconds(100))=>"养老保险基金",
        _=>null
    };
}

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
public sealed record EquityReview(string Id,DateOnly ReportAsOf,DateOnly Published,string DocumentUrl,string Page,string Reviewer,bool Comparable,string Nature,string ShareClass="A",string PercentBasis="TotalShares",string Account="",bool OriginalConfirmed=false,string? Institution=null);

public static class DirectEquityComparison
{
    public static (string State,decimal? Delta) Compare(decimal? previous,decimal? current,bool comparable,bool previousKnown)
        =>current is null?("NotListed",null):!previousKnown || previous is null?("FirstListed",null):!comparable?("NotComparable",null)
            :current>previous?("Increased",current-previous):current<previous?("Decreased",current-previous):("Unchanged",0m);
}
