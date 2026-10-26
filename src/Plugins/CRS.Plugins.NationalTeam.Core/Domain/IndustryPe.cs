namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>官网公布的行业统计值；滚动/静态、加权/中位数分开，不当作含亏损公司的整体估值。</summary>
public sealed record IndustryPeValues(string Code,string Name,int CompanyCount,decimal? RollingWeighted,decimal? RollingMedian,decimal? StaticWeighted,decimal? StaticMedian);
public sealed record IndustryPeRevision(string Id,DateOnly Date,IndustryPeValues Values,int Revision,string State,
    DateTimeOffset FirstSeenAt,DateTimeOffset StateRecordedAt,string CaptureId,bool EvidenceHealthy);
public sealed record IndustryPeChartPoint(DateOnly Date,decimal? RollingWeighted,decimal? RollingMedian,string State);
public sealed record IndustryPeSyncReceipt(Guid RequestId,string State,string Code,string? ExecutionId);
public sealed record IndustryPeSeries(string Code,string Name,IReadOnlyList<IndustryPeChartPoint> Curve,decimal? LatestRollingWeighted,decimal? LatestRollingMedian,
    decimal? PublishedSeriesPercentile,int PriorSamples,int ExpectedPriorDays,string State,DateOnly? LatestDate)
{
    // 附加字段兼容既有报告；旧Worker不提供时保持null，而不是假设中位数也低。
    public decimal? PublishedMedianSeriesPercentile {get;init;}
    public int MedianPriorSamples {get;init;}
}

/// <summary>仅描述官网剔亏样本PE的历史位置，永远不是行业低估结论。</summary>
public enum IndustryPeObservation { EvidenceIncomplete, DualHistoricalLow, MetricDivergence, SingleMetricLow, NoDualLow }
public static class IndustryPePosition
{
    public const decimal LowPercentile=20m; // 研究阈值，未完成样本外有效性检验。
    public static IndustryPeObservation Classify(IndustryPeSeries row)
    {
        if(row.State!="PublishedSeriesResearch"||row.LatestRollingWeighted is not >0m||row.LatestRollingMedian is not >0m
            ||row.PublishedSeriesPercentile is not {} weighted||row.PublishedMedianSeriesPercentile is not {} median
            ||row.ExpectedPriorDays<252||row.PriorSamples<252||row.MedianPriorSamples<252
            ||row.PriorSamples>row.ExpectedPriorDays||row.MedianPriorSamples>row.ExpectedPriorDays
            ||(decimal)row.PriorSamples/row.ExpectedPriorDays<0.8m||(decimal)row.MedianPriorSamples/row.ExpectedPriorDays<0.8m
            ||weighted<0||weighted>100||median<0||median>100)return IndustryPeObservation.EvidenceIncomplete;
        if(weighted<=LowPercentile&&median<=LowPercentile)return IndustryPeObservation.DualHistoricalLow;
        if((weighted<=LowPercentile&&median>=60m)||(median<=LowPercentile&&weighted>=60m))
            return IndustryPeObservation.MetricDivergence;
        if(weighted<=LowPercentile||median<=LowPercentile)return IndustryPeObservation.SingleMetricLow;
        return IndustryPeObservation.NoDualLow;
    }
}
/// <summary>这是公布的剔亏样本序列研究，不填造PB、市值覆盖率、价格指数或盈利证据。</summary>
public sealed record IndustryPeReport(DateOnly From,DateOnly Through,DateTimeOffset Cutoff,string Fingerprint,bool CalendarVerified,IReadOnlyList<IndustryPeSeries> Rows)
{
    public string Method=>"cnindex-pe-2021-04/cninfo-008200/mainland/level1";
    public string OpportunityState=>"NotEvaluatedMissingCoveragePbFundamentals";
    public string TimingProof=>"NotProven";
    public string KnowledgeMode=>"CurrentKnowledgeResearch";
    public string Basis=>"国证公布的剔亏行业样本滚动PE；区间分位不是全行业合理价值或买入信号，PB、市值覆盖率和基本面未知。";
}

/// <summary>只对已登记原件及固定知识时点计算；缺失、冲突、未核验日历分别保留，不连线填补。</summary>
public static class IndustryPeResearch
{
    public static IReadOnlyDictionary<string,string> Sectors {get;}=new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(new Dictionary<string,string>
    { ["C01"]="能源",["C02"]="原材料",["C03"]="工业",["C04"]="可选消费",["C05"]="主要消费",["C06"]="医药卫生",["C07"]="金融",["C08"]="信息技术",["C09"]="电信业务",["C10"]="公用事业",["C11"]="房地产" });
    public static IndustryPeReport Calculate(DateOnly from,DateOnly through,DateTimeOffset cutoff,IReadOnlyList<IndustryPeRevision> revisions,IReadOnlyDictionary<DateOnly,bool?> calendar,string calendarFingerprint)
    {
        if(from>through||through.DayNumber-from.DayNumber>400||cutoff==default||through>DateOnly.FromDateTime(cutoff.ToOffset(TimeSpan.FromHours(8)).DateTime))
            throw new InvalidDataException("行业PE研究区间或知识截止无效。");
        // 复制调用方集合，计算与指纹不能各自读取到不同的输入版本。
        var rows=revisions.ToArray();var days=calendar.OrderBy(x=>x.Key).ToArray();
        var natural=Enumerable.Range(0,through.DayNumber-from.DayNumber+1).Select(from.AddDays).ToArray();
        var mapped=days.ToDictionary(x=>x.Key,x=>x.Value);var verified=calendarFingerprint.Length==64&&calendarFingerprint.All(Uri.IsHexDigit)&&natural.All(d=>mapped.TryGetValue(d,out var value)&&value.HasValue);
        if(rows.Any(x=>x.Date<from||x.Date>through||!Sectors.TryGetValue(x.Values.Code,out var name)||name!=x.Values.Name
            ||x.FirstSeenAt>x.StateRecordedAt||x.StateRecordedAt>cutoff))throw new InvalidDataException("行业PE事实与查询范围、分类或知识时点不一致。");
        if(rows.Any(x=>(x.State is "Accepted" or "NeedsReview")&&!x.EvidenceHealthy))throw new InvalidDataException("行业PE原件不健康，拒绝计算。");
        // 日历未核验仍展示已取得的公布值，但不能补出交易日分母或计算历史分位。
        var axis=verified?natural.Where(d=>mapped[d]==true).ToArray():rows.Select(x=>x.Date).Distinct().Order().ToArray();
        var series=new List<IndustryPeSeries>();
        foreach(var sector in Sectors)
        {
            var groups=rows.Where(x=>x.Values.Code==sector.Key).GroupBy(x=>x.Date).ToDictionary(x=>x.Key,x=>x.ToArray());
            var curve=axis.Select(day=>
            {
                var versions=groups.GetValueOrDefault(day)??[];var accepted=versions.Where(x=>x.State=="Accepted").ToArray();
                if(accepted.Length>1)throw new InvalidDataException("行业PE同日多个已接受版本，不能择优。");
                var pending=versions.Any(x=>x.State=="NeedsReview");var value=!pending&&accepted.Length==1?accepted[0].Values:null;
                return new IndustryPeChartPoint(day,value?.RollingWeighted,value?.RollingMedian,pending?"NeedsReview":value is null?"Missing":"Published");
            }).ToArray();
            var latest=curve.LastOrDefault();
            // 官方NA、零和负值不代表可比较的正PE；两种统计口径分别检查历史覆盖。
            var previous=curve.Take(Math.Max(0,curve.Length-1)).ToArray();
            var history=previous.Where(x=>x.RollingWeighted>0m).Select(x=>x.RollingWeighted!.Value).ToArray();
            var medianHistory=previous.Where(x=>x.RollingMedian>0m).Select(x=>x.RollingMedian!.Value).ToArray();
            var expected=Math.Max(0,axis.Length-1);
            decimal? Rank(decimal? current,decimal[] prior)=>verified&&current is >0m&&prior.Length>=252&&expected>0
                &&(decimal)prior.Length/expected>=0.8m
                ?(prior.Count(x=>x<current.Value)+prior.Count(x=>x==current.Value)/2m)/prior.Length*100m:null;
            var rank=Rank(latest?.RollingWeighted,history);
            var medianRank=Rank(latest?.RollingMedian,medianHistory);
            var state=!verified?"CalendarUnavailable":latest is null?"NoData":latest.State!="Published"?latest.State:rank is null?"InsufficientComparableHistory":"PublishedSeriesResearch";
            series.Add(new(sector.Key,sector.Value,Array.AsReadOnly(curve),latest?.RollingWeighted,latest?.RollingMedian,rank,history.Length,expected,state,latest?.Date)
            {
                PublishedMedianSeriesPercentile=medianRank,MedianPriorSamples=medianHistory.Length
            });
        }
        // 业务管道可用北京时间展示同一时刻，指纹仍统一UTC，避免仅改变时区表示就失去复算身份。
        var frozen=new{Version="industry-pe-report-v3",from,through,Cutoff=cutoff.ToUniversalTime(),calendarFingerprint,
            Revisions=rows.OrderBy(x=>x.Date).ThenBy(x=>x.Values.Code,StringComparer.Ordinal).ThenBy(x=>x.Id,StringComparer.Ordinal)
                .Select(x=>x with{FirstSeenAt=x.FirstSeenAt.ToUniversalTime(),StateRecordedAt=x.StateRecordedAt.ToUniversalTime()}).ToArray(),Calendar=days};
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(frozen)));
        return new(from,through,cutoff,hash,verified,series.AsReadOnly());
    }
}
