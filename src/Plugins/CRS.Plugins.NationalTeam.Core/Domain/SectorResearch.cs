using System.Security.Cryptography;
using System.Text.Json;

namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>行业事实自己的证据身份；价格、估值和基本面不得共用虚构来源或倒填首见时刻。</summary>
public sealed record SectorEvidence(string RevisionId,string Hash,DateTimeOffset FirstSeenAt,DateTimeOffset RetrievedAt,DateTimeOffset AcceptedAt,string State,DateTimeOffset? PublishedAt=null);
/// <summary>分类及价格口径显式版本化，避免把申万/中证、价格/全收益、不同币种混在同一比较图。</summary>
public sealed record SectorDefinition(string Id,string Name,string Scheme,string SchemeVersion,string PriceBasis,string Currency,string ValuationMethod,string UniverseDefinition,DateOnly ValidFrom,DateOnly ValidThrough);
public sealed record SectorPrice(DateOnly Date,decimal Close,SectorEvidence Evidence);
/// <summary>PE/PB保留适用性与市值覆盖率；亏损或负净资产不强行解释成便宜。</summary>
public sealed record SectorValuation(DateOnly Date,decimal? PeTtm,decimal? Pb,bool PeMeaningful,bool PbMeaningful,decimal? Coverage,string Method,string UniverseDefinition,string UniverseSnapshotHash,SectorEvidence Evidence);
public sealed record SectorFundamentals(DateOnly AsOf,decimal? ProfitGrowth,decimal? Coverage,SectorEvidence Evidence);
public sealed record SectorInput(SectorDefinition Definition,IReadOnlyList<SectorPrice> Prices,IReadOnlyList<SectorValuation> Valuations,IReadOnlyList<SectorFundamentals> Fundamentals);
/// <summary>交易日必须来自已核对日历；历史样本和图表使用同一实际日期轴，不用工作日猜补。</summary>
public sealed record SectorStudyRequest(DateOnly From,DateOnly Through,DateTimeOffset Cutoff,DateOnly CalendarFrom,DateOnly CalendarThrough,SectorEvidence CalendarEvidence,
    IReadOnlyList<DateOnly> TradingDays,IReadOnlyList<SectorInput> Sectors,int HistoryYears=5,int MinimumSamples=252,decimal MinimumCoverage=0.8m,decimal LowPercentile=20m)
{
    // 可选新增：未接入真实独立基本面时必须保持空集合，不能将PE低位冒充质量审查通过。
    public IReadOnlyList<IndustryQualityInput> QualityInputs {get;init;}=[];
}
public sealed record SectorChartPoint(DateOnly Date,decimal? NormalizedClose,decimal? PePercentile=null,decimal? PbPercentile=null);
public sealed record SectorStudyRow(string Id,string Name,IReadOnlyList<SectorChartPoint> Curve,decimal? PeTtm,decimal? Pb,decimal? PePercentile,decimal? PbPercentile,
    int PeSamples,int PbSamples,int ExpectedHistory,string PriceState,string ValuationState,string OpportunityState,string Explanation)
{
    public string QualityReadiness {get;init;}="MissingQualitySource";
    public string? QualityFingerprint {get;init;}
}
public sealed record SectorStudyReport(DateOnly BaseDate,DateOnly Through,DateTimeOffset Cutoff,string Fingerprint,IReadOnlyList<SectorStudyRow> Rows)
{
    public string Mode=>"CurrentKnowledgeResearch";
    public string TimingProof=>"NotProven";
    public string Basis=>"行业自身历史相对估值与观察条件；不是绝对合理价值、收益概率或国家队持仓证明。";
}

/// <summary>只计算行业研究投影，不联网、不持久化、不生成订单；走势低点与低估状态分别计算。</summary>
public static class SectorResearch
{
    public const string AlgorithmVersion="sector-study-v2";
    public static SectorStudyReport Calculate(SectorStudyRequest request)
    {
        // IReadOnlyList不保证底层不可变；入口先复制，防止校验、绘图和指纹读取到调用方的不同版本。
        request=request with{TradingDays=request.TradingDays.ToArray(),Sectors=request.Sectors.Select(x=>x with
            {Prices=x.Prices.ToArray(),Valuations=x.Valuations.ToArray(),Fundamentals=x.Fundamentals.ToArray()}).ToArray(),
            QualityInputs=(request.QualityInputs??[]).Select(x=>x with{Observations=x.Observations.ToArray()}).ToArray()};
        if(request.From>request.Through||request.Through>DateOnly.FromDateTime(request.Cutoff.ToOffset(TimeSpan.FromHours(8)).DateTime)
            ||request.HistoryYears is <1 or >20||request.MinimumSamples<252||request.MinimumCoverage is <=0 or >1||request.LowPercentile is <=0 or >=50
            ||request.Sectors.Count>100||request.Sectors.Select(x=>x.Definition.Id).Distinct(StringComparer.Ordinal).Count()!=request.Sectors.Count
            ||request.QualityInputs.Select(x=>x.SectorId).Distinct(StringComparer.Ordinal).Count()!=request.QualityInputs.Count
            ||request.QualityInputs.Any(x=>!request.Sectors.Any(sector=>sector.Definition.Id==x.SectorId))
            ||request.TradingDays.Distinct().Count()!=request.TradingDays.Count)throw new InvalidDataException("行业研究日期、规则或样本身份无效。");
        var calendar=request.TradingDays.Order().ToArray();var calendarSet=calendar.ToHashSet();var dates=calendar.Where(x=>x>=request.From&&x<=request.Through).ToArray();
        if(dates.Length==0||request.CalendarFrom>request.From||request.CalendarThrough<request.Through||!Known(request.CalendarEvidence,request.Cutoff))
            throw new InvalidDataException("行业图表缺少已核对的交易日历。");
        // 整张图用相同起点和口径；不能为缺起点的行业偷偷选择另一个日期归一化。
        var baseDate=dates[0];var definitions=request.Sectors.Select(x=>x.Definition).ToArray();
        if(definitions.Select(x=>(x.Scheme,x.SchemeVersion,x.PriceBasis,x.Currency)).Distinct().Count()>1
            ||definitions.Any(x=>string.IsNullOrWhiteSpace(x.Id)||string.IsNullOrWhiteSpace(x.Name)||string.IsNullOrWhiteSpace(x.SchemeVersion)
                ||string.IsNullOrWhiteSpace(x.Scheme)||string.IsNullOrWhiteSpace(x.PriceBasis)||string.IsNullOrWhiteSpace(x.Currency)))
            throw new InvalidDataException("不同行业分类、版本、币种或价格口径不能直接叠图。");
        var historyFrom=dates[^1].AddYears(-request.HistoryYears);var expected=calendar.Count(x=>x>=historyFrom&&x<dates[^1]);
        var historyCalendar=request.CalendarFrom<=historyFrom&&request.CalendarThrough>=dates[^1];
        var rows=new List<SectorStudyRow>();
        var qualityBySector=request.QualityInputs.ToDictionary(x=>x.SectorId,StringComparer.Ordinal);
        foreach(var sector in request.Sectors.OrderBy(x=>x.Definition.Id,StringComparer.Ordinal))
        {
            if(sector.Prices.GroupBy(x=>x.Date).Any(x=>x.Count()!=1)||sector.Valuations.GroupBy(x=>x.Date).Any(x=>x.Count()!=1)
                ||sector.Fundamentals.GroupBy(x=>x.AsOf).Any(x=>x.Count()!=1))throw new InvalidDataException("同日行业修订冲突必须先审核，不能任意择优。");
            var definition=sector.Definition;
            var mapped=definition.ValidFrom<=baseDate&&definition.ValidThrough>=dates[^1];
            var prices=sector.Prices.Where(x=>x.Close>0&&Known(x.Evidence,request.Cutoff)).ToDictionary(x=>x.Date);
            var hasBase=mapped&&prices.TryGetValue(baseDate,out _);var baseline=hasBase?prices[baseDate].Close:1m;
            var curve=dates.Select(d=>new SectorChartPoint(d,hasBase&&prices.TryGetValue(d,out var p)?checked(p.Close/baseline*100m):null)).ToArray();
            var priceState=!mapped?"MappingUnavailable":!hasBase?"MissingCommonBase":curve.Any(x=>x.NormalizedClose is null)?"Gaps":"Complete";
            bool ValuationKnown(SectorValuation value)=>Known(value.Evidence,request.Cutoff)&&value.Date>=definition.ValidFrom&&value.Date<=definition.ValidThrough
                &&!string.IsNullOrWhiteSpace(definition.ValuationMethod)&&!string.IsNullOrWhiteSpace(definition.UniverseDefinition)&&value.Coverage>=request.MinimumCoverage&&value.Coverage<=1m
                &&value.Method==definition.ValuationMethod&&value.UniverseDefinition==definition.UniverseDefinition&&Hash(value.UniverseSnapshotHash);
            var current=sector.Valuations.SingleOrDefault(x=>x.Date==dates[^1]);
            var eligible=sector.Valuations.Where(x=>calendarSet.Contains(x.Date)&&ValuationKnown(x)).ToArray();
            var history=eligible.Where(x=>x.Date>=historyFrom&&x.Date<dates[^1]).ToArray();
            var pe=history.Where(x=>x.PeMeaningful&&x.PeTtm>0).Select(x=>x.PeTtm!.Value).ToArray();
            var pb=history.Where(x=>x.PbMeaningful&&x.Pb>0).Select(x=>x.Pb!.Value).ToArray();
            var latestValid=mapped&&current is not null&&ValuationKnown(current);
            var currentPe=latestValid&&current!.PeMeaningful&&current.PeTtm>0?current.PeTtm:null;
            var currentPb=latestValid&&current!.PbMeaningful&&current.Pb>0?current.Pb:null;
            bool Enough(int count)=>historyCalendar&&expected>0&&count>=request.MinimumSamples&&(decimal)count/expected>=request.MinimumCoverage;
            var peRank=currentPe is {} pval&&Enough(pe.Length)?Rank(pval,pe):(decimal?)null;
            var pbRank=currentPb is {} bval&&Enough(pb.Length)?Rank(bval,pb):(decimal?)null;
            // 分位曲线每日只使用该日之前的同口径样本；使用当前知识版本展示历史，不能当作历史实盘回测。
            decimal? DailyRank(DateOnly day,bool earnings)
            {
                var value=eligible.SingleOrDefault(x=>x.Date==day);var start=day.AddYears(-request.HistoryYears);
                var population=calendar.Count(x=>x>=start&&x<day);
                var sample=eligible.Where(x=>x.Date>=start&&x.Date<day&&(earnings?x.PeMeaningful&&x.PeTtm>0:x.PbMeaningful&&x.Pb>0))
                    .Select(x=>earnings?x.PeTtm!.Value:x.Pb!.Value).ToArray();
                var currentValue=value is null?null:earnings?value.PeMeaningful?value.PeTtm:null:value.PbMeaningful?value.Pb:null;
                return request.CalendarFrom<=start&&currentValue>0&&population>0&&sample.Length>=request.MinimumSamples&&(decimal)sample.Length/population>=request.MinimumCoverage
                    ?Rank(currentValue.Value,sample):null;
            }
            curve=curve.Select(x=>x with{PePercentile=DailyRank(x.Date,true),PbPercentile=DailyRank(x.Date,false)}).ToArray();
            var valuationState=current is null?"MissingCurrentValuation":!latestValid?"UnverifiedValuation":currentPe is null||currentPb is null?"NotMeaningful"
                :peRank is null||pbRank is null?"InsufficientHistory":"ComparableHistory";
            var fundamental=sector.Fundamentals.Where(x=>x.AsOf<=dates[^1]&&Known(x.Evidence,request.Cutoff)).OrderByDescending(x=>x.AsOf).FirstOrDefault();
            var fundamentalValid=fundamental is not null&&dates[^1].DayNumber-fundamental.AsOf.DayNumber<=180&&fundamental.Coverage>=request.MinimumCoverage&&fundamental.Coverage<=1m&&fundamental.ProfitGrowth.HasValue;
            var quality=IndustryQualityGate.Evaluate(definition,qualityBySector.GetValueOrDefault(definition.Id),
                dates[^1],request.Cutoff,current?.UniverseSnapshotHash);
            var low=peRank<=request.LowPercentile&&pbRank<=request.LowPercentile;
            var opportunity=valuationState!="ComparableHistory"?"NotEvaluated":!low?"NoLowValuationCondition":priceState!="Complete"?"PriceDataIncomplete":!fundamentalValid?"FundamentalsUnavailable"
                :fundamental!.ProfitGrowth<0?"ValueTrapRisk":quality.State!="ReadyForAdversarialReview"?"IndependentQualityEvidenceUnavailable":"NeedsAdversarialAssessment";
            rows.Add(new SectorStudyRow(definition.Id,definition.Name,Array.AsReadOnly(curve),currentPe,currentPb,peRank,pbRank,pe.Length,pb.Length,expected,priceState,valuationState,opportunity,
                "估值低位不是合理价值；基本面审核仅代表质量证据齐备，不能证实低估或构成买入信号。")
                {QualityReadiness=quality.State,QualityFingerprint=quality.Fingerprint});
        }
        // 指纹绑定来源、审核/首见、分类版本和参数；同一数值换证据或知识时点也不是同一研究输入。
        var frozen=request with{TradingDays=calendar,Sectors=request.Sectors.OrderBy(x=>x.Definition.Id,StringComparer.Ordinal).Select(x=>x with
            {Prices=x.Prices.OrderBy(p=>p.Date).ToArray(),Valuations=x.Valuations.OrderBy(v=>v.Date).ToArray(),Fundamentals=x.Fundamentals.OrderBy(f=>f.AsOf).ToArray()}).ToArray(),
            QualityInputs=request.QualityInputs.OrderBy(x=>x.SectorId,StringComparer.Ordinal)
                .Select(x=>x with{Observations=x.Observations.OrderBy(y=>y.Measure).ToArray()}).ToArray()};
        var fingerprint=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AlgorithmVersion+"|"+JsonSerializer.Serialize(frozen))));
        return new(baseDate,dates[^1],request.Cutoff,fingerprint,Array.AsReadOnly(rows.ToArray()));
    }
    private static decimal Rank(decimal current,IReadOnlyList<decimal> history)=>(history.Count(x=>x<current)+history.Count(x=>x==current)/2m)/history.Count*100m;
    private static bool Hash(string value)=>value.Length==64&&value.All(Uri.IsHexDigit);
    private static bool Known(SectorEvidence value,DateTimeOffset cutoff)=>value.State=="Accepted"&&Hash(value.Hash)&&!string.IsNullOrWhiteSpace(value.RevisionId)
        &&value.RetrievedAt!=default&&value.RetrievedAt<=value.FirstSeenAt&&value.FirstSeenAt<=value.AcceptedAt&&value.AcceptedAt<=cutoff
        &&(value.PublishedAt is null||value.PublishedAt<=cutoff);
}
