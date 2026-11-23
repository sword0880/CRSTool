namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>客户端接受的已知候选解析版本；没有声明的版本不能当作已核验表格读取。</summary>
public static class EtfHolderParserVersions
{
    public const string Huatai300="huatai300-holders-pdf-v1";
    public const string EfundChiNext="efund-chinext-holders-pdf-v1";
    public const string HarvestCsi300="harvest-csi300-holders-pdf-v1";
    public const string China50="china50-holders-pdf-v1";
}

/// <summary>报告表格的登记持有人候选；不推断受益人、独立持有关系或成交。</summary>
public sealed record EtfHolderCandidate(int Rank,string RegisteredHolder,string RawHolder,decimal Shares,decimal PercentOfListedUnits,
    string EvidencePage,string? SuggestedInstitution)
{
    public string State=>"NeedsReview";
    public bool OriginalConfirmed=>false;
    public bool IndependentOwnershipConfirmed=>false;
}

/// <summary>已存原件的只读解析报告；未知模板、日粒度公告和人工核验分别保留。</summary>
public sealed record EtfHolderCandidateReport(AlertDocument Document,string ParserVersion,string State,string Explanation,
    DateOnly? ReportAsOf,DateOnly? PublicationDate,int? PhysicalPage,string? Unit,string? PercentBasis,string? Scope,
    IReadOnlyList<EtfHolderCandidate> Holders)
{
    public DateTimeOffset? PublishedAt=>null;
}

/// <summary>后台应用取得的冻结原件，不通过界面分页传输整份PDF。</summary>
public sealed record EtfHolderDocument(AlertDocument Document,byte[] Content);
