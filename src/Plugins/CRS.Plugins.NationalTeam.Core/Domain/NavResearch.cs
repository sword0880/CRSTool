namespace CRS.Plugins.NationalTeamWatch.Domain;

public sealed record NavResearchSource(string Url, string Sha256, DateTimeOffset RetrievedAt, int Bytes);
public sealed record NavResearchPoint(DateOnly Date, decimal UnitNavYuan, decimal AccumulatedNavYuan,
    DateTimeOffset FirstSeenAt, IReadOnlyList<string> SourceHashes)
{
    // 净值日期不是公告时刻；原件没有逐日公布时间时必须保持未知。
    public DateTimeOffset? PublishedAt => null;
}
public sealed record NavResearchDataset(string Market, string Ticker, string ParserVersion, DateTimeOffset VerifiedAt,
    string Fingerprint, IReadOnlyList<NavResearchSource> Sources, IReadOnlyList<NavResearchPoint> Points)
{
    public string KnowledgeMode => "RetrospectiveOnly";
    public string CalendarState => "NotVerified";
    public string TimingProof => "NotProven";
    public string Basis => "官方单位净值历史研究样本；不是成交价、机构成交或已验证的资金流。";
}
