using System.Text.Json;
using CRS.Timing;
using CRS.Common.Integrity;

namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>保留真实原件与登记身份；汇总指纹不能冒充交易所原件摘要。</summary>
public sealed record MarketCycleSourceReference(string Component,DateOnly Date,string RevisionId,string Role,
    string SourceUrl,string Hash,string OriginalHash,DateTimeOffset RetrievedAt,DateTimeOffset FirstSeenAt,
    DateTimeOffset StateRecordedAt,string State)
{
    public string ComponentText=>Component switch{"Price"=>"指数价格","Activity"=>"市场成交","Breadth"=>"市场广度","Calendar"=>"交易日历",_=>Component};
    public string StateText=>State switch{"Accepted"=>"已核验","NeedsReview"=>"差异待审","Critical"=>"证据异常",_=>State};
    public string RoleText=>Role switch{"Price"=>"价格原件","Method"=>"指数方法","Units"=>"单位说明","ExchangeOriginal"=>"交易所原件","ReviewedNormalizedArchive"=>"人工核验档案","SSEAnnualNotice"=>"上交所年度公告","SZSEAnnualNotice"=>"深交所年度公告",_=>Role};
}

/// <summary>完整冻结输入可供复算；研究规则尚未通过择时验证，查询也不产生通知。</summary>
public sealed record MarketCycleStudyReport(MarketCycleInput Input,MarketCycleRules Rules,MarketCycleAssessment Assessment,
    IReadOnlyList<MarketCycleSourceReference> Sources,string ActivityKnowledgeFingerprint,string Fingerprint)
{
    public const string Version="market-cycle-study-v1";
    public string FormatVersion {get;init;}=Version;
    public string PriceScope=>"国证A指399317：沪深北符合方法要求的A股样本（剔除ST等），作为价格代理。";
    public string ActivityScope=>"沪深交易所股票市场成交额及股数；两市各自审核后按日合计。";
    public string BreadthScope=>"沪深A股当日证券宇宙；当日口径复权收盘、MA20、MA60及前250日最低收盘。";
    public string EvidenceKind=>"DerivedInputFingerprintWithSeparateOriginalReferences";
    public bool NotificationsEnabled=>false;
    public string CalendarReplayProof=>"CurrentReviewedPointerOnlyNotProven";
    public string HistoricalIndexMethodProof=>"NotVerified";
    public string BreadthUniverseProof=>"ManualReviewOnly";
    public int ActivityReadySessions=>Input.TradingDays.Where(x=>x<=Input.AsOfDate).Order().TakeLast(21)
        .Count(d=>Input.ActivityPoints.Count(x=>x.Date==d)==1&&Input.ActivityPoints.Any(x=>x.Date==d&&x.State==MarketCycleObservationState.Accepted&&x.AmountYuan.HasValue&&x.VolumeShares.HasValue));
    public int BreadthReadySessions=>Input.TradingDays.Where(x=>x<=Input.AsOfDate).Order().TakeLast(6)
        .Count(d=>Input.BreadthPoints.Count(x=>x.Date==d)==1&&Input.BreadthPoints.Any(x=>x.Date==d&&x.State==MarketCycleObservationState.Accepted&&x.UniverseCount>0));
    public static MarketCycleStudyReport Freeze(MarketCycleInput input,IReadOnlyList<MarketCycleSourceReference> sources,string activityFingerprint,MarketCycleRules? rules=null)
    {
        rules=(rules??new()).Validate();
        // 统一显示时区；领域指纹仍以UTC时刻构造，等价截止时点有相同身份。
        input=input with{EvaluationCutoff=BeijingClock.Normalize(input.EvaluationCutoff),
            IndexPoints=input.IndexPoints.Select(x=>x with{SourcePublishedAt=x.SourcePublishedAt is {} p?BeijingClock.Normalize(p):null,FirstSeenAt=BeijingClock.Normalize(x.FirstSeenAt),RetrievedAt=BeijingClock.Normalize(x.RetrievedAt)}).ToArray(),
            ActivityPoints=input.ActivityPoints.Select(x=>x with{SourcePublishedAt=x.SourcePublishedAt is {} p?BeijingClock.Normalize(p):null,FirstSeenAt=BeijingClock.Normalize(x.FirstSeenAt),RetrievedAt=BeijingClock.Normalize(x.RetrievedAt)}).ToArray(),
            BreadthPoints=input.BreadthPoints.Select(x=>x with{SourcePublishedAt=x.SourcePublishedAt is {} p?BeijingClock.Normalize(p):null,FirstSeenAt=BeijingClock.Normalize(x.FirstSeenAt),RetrievedAt=BeijingClock.Normalize(x.RetrievedAt)}).ToArray()};
        sources=sources.OrderBy(x=>x.Component,StringComparer.Ordinal).ThenBy(x=>x.Date).ThenBy(x=>x.RevisionId,StringComparer.Ordinal).ThenBy(x=>x.Role,StringComparer.Ordinal)
            .Select(x=>x with{RetrievedAt=BeijingClock.Normalize(x.RetrievedAt),FirstSeenAt=BeijingClock.Normalize(x.FirstSeenAt),StateRecordedAt=BeijingClock.Normalize(x.StateRecordedAt)}).ToArray();
        var assessment=MarketCycleEvaluator.Evaluate(input,rules);
        var hash=ContentFingerprint.Sha256(JsonSerializer.SerializeToUtf8Bytes(new{Version,Input=input,Rules=rules,Assessment=assessment,Sources=sources,ActivityKnowledgeFingerprint=activityFingerprint}));
        return new(input,rules,assessment,sources,activityFingerprint,hash);
    }
}

/// <summary>人工核验的标准化广度档案，逐证券值由后台比较并计数，不接收前端计算的比例。</summary>
public sealed record MarketBreadthArchive(string Version,DateOnly Date,string UniverseDefinitionVersion,string UniverseSnapshotId,
    DateOnly UniverseDate,string PriceBasis,IReadOnlyList<string> Members,IReadOnlyList<MarketBreadthMember> Observations);
public sealed record MarketBreadthMember(string Security,decimal Close,decimal Ma20,decimal Ma60,decimal Prior250Low);
