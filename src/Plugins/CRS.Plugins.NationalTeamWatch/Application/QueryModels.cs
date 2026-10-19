using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>公开采集阶段，不含原件、凭据、任意异常消息或完整请求URL。</summary>
public sealed record EtfProgress(string Stage,DateOnly? Date=null,string? Market=null,int Received=0,int Added=0,int Duplicates=0,int Pending=0,int? HttpStatus=null,DateTimeOffset? Until=null);
public sealed record ShareImportOutcome(int Added,int Duplicates,int Pending);
public sealed record ShareGap(EtfKey Key,DateOnly Date,DateTimeOffset? LastAttempt);
public sealed record EtfAcceptance(string Market,string Ticker,DateOnly Through,DateOnly? TargetFrom,int ExpectedDays,int AcceptedDays,int UnresolvedDays,int ContinuousCollectionDays,bool CalendarVerified)
{
    public bool History250Passed=>CalendarVerified&&ExpectedDays>=250&&AcceptedDays==ExpectedDays&&UnresolvedDays==0;
    public bool Continuous20Passed=>CalendarVerified&&ContinuousCollectionDays>=20;
    public bool DataAcceptancePassed=>History250Passed&&Continuous20Passed;
}
/// <summary>候选目录与实际监控分开，停用条目仍可查询和编辑，不删除历史事实。</summary>
public sealed record EtfConfiguration(string Market,string Ticker,string Name,string IndexGroup,bool Active,string Reason,string Origin,long Revision)
{
    public string StateText=>Active?"监控中":"未监控";
}
/// <summary>只读概览；未知指标保持空值，不用零替代缺失数据。</summary>
public sealed record EtfSummary(string Market, string Ticker, string Name, string IndexGroup, string Label,
    DateOnly? AsOf, decimal? Shares, decimal? Change1, decimal? Change5, decimal? Change20, string Status, int CoveredDays)
{
    public EvidenceLevel EvidenceLevel=>EvidenceLevel.MarketObservation;
    public string EvidenceText=>EvidenceSemantics.Text(EvidenceLevel);
    public string Basis=>EvidenceSemantics.EtfBasis;
}
/// <summary>事实修订的双时间轴和两份原始证据哈希，用于审计与复算。</summary>
public sealed record EvidenceRow(string RevisionId, string Market, string Ticker, string TradeDate, string Shares,
    string State, int Revision, string Hash, string OriginalHash, string SourceUrl, string RetrievedAt, string FirstSeenAt, string? AcceptedAt, string? Supersedes)
{
    public EvidenceLevel EvidenceLevel=>EvidenceLevel.MarketObservation;
    public string EvidenceText=>EvidenceSemantics.Text(EvidenceLevel);
    public string Basis=>EvidenceSemantics.EtfBasis;
}
/// <summary>保留日历中的每个日期边界，区分缺失、待审核、拒绝和损坏。</summary>
public sealed record CollectionAttempt(string Id,string Market,string Ticker,DateOnly Date,string State,string Reason,string? FetchId,string UpdatedAt,
    string? SourceUrl=null,string? ParserVersion=null,string? OriginalHash=null)
{
    public string StatusText=>State switch
    {
        "Expected"=>"等待采集", "Fetching"=>"正在获取（若任务已结束则为中断遗留）", "RawSaved"=>"原件已存证", "Parsed"=>"已解析", "Validating"=>"质量校验中",
        "Accepted"=>"本次份额已验证", "NotPublished"=>"本次未返回份额", "AccessBlocked"=>"访问受限", "ParseFailed"=>"解析或校验失败",
        "Missing"=>"无份额，日历待核验", "NeedsReview"=>"待人工审核", "Interrupted"=>"采集已中断", _=>"来源请求失败"
    };
}
public sealed record CoverageDay(DateOnly Date,string State,string? RevisionId,CollectionAttempt? LastAttempt=null)
{
    public string AttemptText=>LastAttempt is null?"未登记采集尝试":$"{LastAttempt.StatusText} · {LastAttempt.UpdatedAt}";
    /// <summary>状态代码供测试和审计使用，界面展示对应的中文业务含义。</summary>
    public string StatusText=>State switch {"Accepted"=>"有效份额", "Missing"=>"缺少份额", "NeedsReview"=>"待人工审核", "Critical"=>"份额证据异常", "CalendarCritical"=>"日历证据异常", "QualityRejected"=>"审核拒绝", _=>"未知"};
}
/// <summary>覆盖率只针对已登记日历，不代表交易所完整历史或250日目标已经达到。</summary>
public sealed record CoverageReport(string Market,string Ticker,DateOnly From,DateOnly To,int RegisteredTradingDays,
    int AcceptedDays,int MissingDays,int PendingDays,int CriticalDays,int RejectedDays,decimal? CoveragePercent,
    bool CalendarVerified,bool CalendarRangeCovered,string State,IReadOnlyList<CoverageDay> Days)
{
    /// <summary>完整仅指已登记日历口径，不宣称官方全量历史已取得。</summary>
    public string StatusText=>State switch {"NoCalendar"=>"该区间未登记交易日历", "CalendarCritical"=>"日历证据异常，指标暂停", "CalendarRangeIncomplete"=>"所选区间超出已登记日历边界", "CompleteRegisteredCalendar"=>"已登记日历内完整", _=>"份额覆盖不完整"};
}
