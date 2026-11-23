using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>固定核对的公布指数目录。1000行业只代表国证1000样本，不能与全行业PE或国家队持仓共用证券宇宙。</summary>
public sealed record IndexResearchDefinition(string Code,string Name,string? Sector,string Family,string MethodUrl,string MethodHash)
{
    public string PriceBasis=>"OfficialPublishedIndexLevel";
    public string Currency=>"CNY";
    public string Universe=>Family=="Cni1000Sector"?"CNI1000ConstituentsInSector":"CniAShareEligibleConstituentsExST";
    public string HistoricalMethodConsistency=>"NotVerified";
}

public static class IndexResearchCatalog
{
    public const string SectorMethodHash="40D5AFC05865E31E8242A3719542B4BC50AE5AFBD5CA42407C20363912444F31";
    public const string BroadMethodHash="6BFE1E609422F4107F99613E54CFA7F20806E57809D370A564C2C1E73AE96D83";
    private static readonly (string Code,string Name,string Sector)[] SectorItems=[("399381","1000能源","C01"),("399382","1000材料","C02"),("399383","1000工业","C03"),("399384","1000可选","C04"),("399385","1000消费","C05"),("399386","1000医药","C06"),("399387","1000金融","C07"),("399388","1000信息","C08"),("399390","1000公用","C10"),("399367","1000地产","C11")];
    // 当前同一份官方方法仅列10条；旧方法中的电信不能按名称或旧代码擅自拼入当前系列。
    public static IReadOnlyDictionary<string,IndexResearchDefinition> Definitions {get;}=new ReadOnlyDictionary<string,IndexResearchDefinition>(
        SectorItems.Select(x=>new IndexResearchDefinition(x.Code,x.Name,x.Sector,"Cni1000Sector","https://www.cnindex.com.cn/docs/gz_399381.pdf",SectorMethodHash))
            .Append(new("399317","国证A指",null,"CniAShare","https://www.cnindex.com.cn/docs/gz_399317.pdf",BroadMethodHash)).ToDictionary(x=>x.Code,StringComparer.Ordinal));
    public static IndexResearchDefinition Get(string code)=>Definitions.TryGetValue(code,out var value)?value:throw new InvalidDataException("指数不在已核对目录中。");
}

/// <summary>日线的成交量/额是指数样本汇总；万手转股、亿元转元，绝不能标成全市场或机构成交。</summary>
public sealed record IndexSeriesValue(string Code,DateOnly Date,string Kind,decimal Close,decimal? Open=null,decimal? High=null,decimal? Low=null,
    decimal? SampleAmountYuan=null,decimal? SampleVolumeShares=null,decimal? PublishedStaticPe=null,decimal? PublishedRollingPe=null,decimal? PublishedPb=null,int? ReportedSamples=null);
public sealed record IndexSeriesRevision(string Id,IndexSeriesValue Value,int Revision,string State,DateTimeOffset FirstSeenAt,DateTimeOffset StateRecordedAt,string CaptureId,bool EvidenceHealthy,string MethodHash);
public sealed record IndexSeriesCommandReceipt(Guid RequestId,string State,string Code,string? ExecutionId);
public sealed record IndexSeriesCurvePoint(DateOnly Date,decimal? NormalizedLevel);
public sealed record IndexSeriesResearchRow(string Code,string Name,string Family,IReadOnlyList<IndexSeriesCurvePoint> Curve,DateOnly? LatestPriceDate,decimal? LatestClose,
    int AcceptedPriceDays,int ExpectedPriceDays,string PriceState,decimal? Drawdown250,DateOnly? ValuationDate,decimal? PublishedRollingPe,decimal? PublishedPb,int PbPriorSamples,string ValuationState)
{
    public string CoverageState=>"Unknown";
    public string OpportunityState=>"NotEvaluatedMissingCoverageFundamentalsAndComparableValuationHistory";
}
public sealed record IndexSeriesResearchReport(DateOnly From,DateOnly Through,DateTimeOffset Cutoff,string Fingerprint,bool CalendarVerified,DateOnly? BaseDate,IReadOnlyList<IndexSeriesResearchRow> Rows)
{
    // 运行时来源准入状态不参与金融报告指纹；只控制新采集，不赋予既有事实额外有效性。
    public bool PriceCollectionAllowed {get;init;}
    public bool ValuationCollectionAllowed {get;init;}
    public string Mode=>"CurrentKnowledgeResearch";
    public string TimingProof=>"NotProven";
    public string InstitutionalTradingEvidence=>"NotObserved";
    public string Scope=>"CNI1000行业样本指数与国证A指；公布指数点位/估值，非全行业估值或国家队成交";
}

public static class IndexSeriesResearch
{
    /// <summary>使用同一核验日期起点；缺日、差异待审和损坏原件均断线，当前知识回看不冒充历史当时已知。</summary>
    public static IndexSeriesResearchReport Calculate(DateOnly from,DateOnly through,DateTimeOffset cutoff,IReadOnlyList<IndexSeriesRevision> revisions,IReadOnlyDictionary<DateOnly,bool?> calendar,string calendarHash)
    {
        if(from>through||through.DayNumber-from.DayNumber>400||cutoff==default||through>DateOnly.FromDateTime(cutoff.ToOffset(TimeSpan.FromHours(8)).DateTime))throw new InvalidDataException("指数研究日期或知识截止无效。");
        var records=revisions.Select(x=>x with{Value=x.Value with{}}).ToArray();
        var natural=Enumerable.Range(0,through.DayNumber-from.DayNumber+1).Select(from.AddDays).ToArray();
        var verified=calendarHash.Length==64&&calendarHash.All(Uri.IsHexDigit)&&natural.All(d=>calendar.TryGetValue(d,out var open)&&open.HasValue);
        var days=natural.Where(d=>calendar.TryGetValue(d,out var open)&&open==true).ToArray();var basis=days.FirstOrDefault();
        bool Known(IndexSeriesRevision x)=>x.FirstSeenAt!=default&&x.StateRecordedAt!=default&&x.FirstSeenAt<=x.StateRecordedAt&&x.StateRecordedAt<=cutoff&&x.Value.Date<=DateOnly.FromDateTime(x.FirstSeenAt.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var rows=new List<IndexSeriesResearchRow>();
        foreach(var definition in IndexResearchCatalog.Definitions.Values.OrderBy(x=>x.Family).ThenBy(x=>x.Code))
        {
            var source=records.Where(x=>x.Value.Code==definition.Code&&Known(x)).ToArray();
            IndexSeriesValue? Value(string kind,DateOnly day)
            {
                var group=source.Where(x=>x.Value.Kind==kind&&x.Value.Date==day).ToArray();
                if(group.Any(x=>x.State is "NeedsReview" or "Critical"||x.State=="Accepted"&&(!x.EvidenceHealthy||x.MethodHash!=definition.MethodHash)))return null;
                var accepted=group.Where(x=>x.State=="Accepted").ToArray();return accepted.Length==1?accepted[0].Value:null;
            }
            // 没有完整日历时只展示实际日期的来源事实，曲线仍可见缺口，但不认可归一比较或250日指标。
            var axis=verified?days:records.Where(x=>x.Value.Date>=from&&x.Value.Date<=through).Select(x=>x.Value.Date).Distinct().Order().ToArray();
            var baseValue=verified&&days.Length>0?Value("Price",basis)?.Close:null;
            var curve=axis.Select(d=>new IndexSeriesCurvePoint(d,baseValue>0&&Value("Price",d) is {} value?value.Close/baseValue*100:null)).ToArray();
            var acceptedDays=days.Count(d=>Value("Price",d) is not null);var latest=days.Length>0?Value("Price",days[^1]):null;
            var priceState=!verified?"CalendarUnavailable":days.Length==0?"NoReferenceTradingDays":baseValue is null?"MissingCommonBase":acceptedDays!=days.Length?"PriceGapsOrPending":"PublishedSeriesResearch";
            var tail=days.TakeLast(250).ToArray();var complete250=verified&&tail.Length==250&&tail.All(d=>Value("Price",d) is not null);
            var high=complete250?tail.Max(d=>Value("Price",d)!.Close):(decimal?)null;
            var valuationDates=source.Where(x=>x.Value.Kind=="Valuation"&&x.Value.Date<=through).Select(x=>x.Value.Date).Distinct().OrderDescending().ToArray();
            var valuation=valuationDates.Length==0?null:Value("Valuation",valuationDates[0]);
            var prior=valuation is null?0:valuationDates.Count(d=>d<valuation.Date&&Value("Valuation",d)?.PublishedPb>0);
            rows.Add(new(definition.Code,definition.Name,definition.Family,curve,latest?.Date,latest?.Close,acceptedDays,days.Length,priceState,
                high>0&&latest is not null?latest.Close/high-1:null,valuation?.Date,valuation?.PublishedRollingPe>0?valuation.PublishedRollingPe:null,valuation?.PublishedPb>0?valuation.PublishedPb:null,prior,
                valuation is null?valuationDates.Length==0?"MissingPublishedSnapshot":"SnapshotPendingOrUnhealthy":"PublishedSnapshotHistoryOrCoverageUnverified"));
        }
        // 时间统一UTC；绑定全部修订、状态、原件健康、方法及日历，不能只散列可见曲线掩盖缺失输入。
        var frozen=new{Version="cnindex-series-report-v1",from,through,Cutoff=cutoff.ToUniversalTime(),calendarHash,Calendar=calendar.OrderBy(x=>x.Key),
            Definitions=IndexResearchCatalog.Definitions.Values.OrderBy(x=>x.Code),Records=records.OrderBy(x=>x.Value.Code).ThenBy(x=>x.Value.Date).ThenBy(x=>x.Id).Select(x=>new{x.Id,x.Value,x.Revision,x.State,FirstSeenAt=x.FirstSeenAt.ToUniversalTime(),StateRecordedAt=x.StateRecordedAt.ToUniversalTime(),x.CaptureId,x.EvidenceHealthy,x.MethodHash})};
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(frozen)));
        return new(from,through,cutoff,fingerprint,verified,verified&&days.Length>0?basis:null,rows.AsReadOnly());
    }
}
