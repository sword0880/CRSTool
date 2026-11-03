namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>原始金额均为百万港元；公布净买额与买卖额、累计额分别保留，不以空值补零。</summary>
public sealed record SouthboundFlowObservation(DateOnly Date,string Channel,decimal? BuyMillionHkd,decimal? SellMillionHkd,
    decimal? NetBuyMillionHkd,decimal? CumulativeNetBuyMillionHkd);
public sealed record SouthboundAnnualPoint(DateOnly Date,int CalendarDay,decimal? NetBuyYiHkd,decimal? CumulativeNetBuyYiHkd,string State);
public sealed record SouthboundYearCurve(int Year,DateOnly? BaselineDate,IReadOnlyList<SouthboundAnnualPoint> Points);
public sealed record SouthboundAnnualReport(int FormatVersion,string Metric,int FirstYear,int LastYear,DateOnly Through,DateTimeOffset? RetrievedAt,
    string? CaptureId,IReadOnlyList<SouthboundYearCurve> Years)
{
    public string Basis=>"南向年内累计净买入＝当年沪深港股通每日买入减卖出之和，单位：亿港元；东方财富整理数据，不代表整个港股市场资金流。";
}

/// <summary>逐年归零累计净买额；前一年末原始累计额核对年初与逐日连续性，缺口后不续算该年。</summary>
public static class SouthboundAnnualComparison
{
    public const int EarliestYear=2017;
    public const int Version=2;
    public const string Metric="YearToDateNetBuyTurnover";
    public static readonly DateOnly SourceFrom=new(2016,12,1);
    // 来源按百万港元保留两位；仅容忍公布字段逐项四舍五入及浮点传输尾差。
    public const decimal RoundingToleranceMillionHkd=0.02m;
    public static void ValidateYears(int first,int last,int currentYear)
    {
        if(first<EarliestYear||last<first||last>currentYear||last-first>9)
            throw new InvalidDataException("请选择2017年以来、最多十个年度的有效区间。");
    }
    public static SouthboundAnnualReport Build(int first,int last,DateOnly through,DateTimeOffset? retrieved,
        string? capture,IReadOnlyList<SouthboundFlowObservation> observations)
    {
        ValidateYears(first,last,through.Year);ValidateObservations(observations,through);
        var years=new List<SouthboundYearCurve>();
        foreach(var year in Enumerable.Range(first,last-first+1))
        {
            var baseline=observations.Where(x=>x.Date.Year==year-1).GroupBy(x=>x.Date).OrderBy(x=>x.Key).LastOrDefault();
            var ready=baseline is not null&&baseline.Count()==2&&baseline.All(x=>x.CumulativeNetBuyMillionHkd.HasValue);
            var previous=ready?baseline!.ToDictionary(x=>x.Channel,x=>x.CumulativeNetBuyMillionHkd!.Value):new Dictionary<string,decimal>();
            var points=new List<SouthboundAnnualPoint>();var state=ready?"Available":"MissingYearBaseline";decimal running=0;
            foreach(var day in observations.Where(x=>x.Date.Year==year).GroupBy(x=>x.Date).OrderBy(x=>x.Key))
            {
                var rows=day.ToArray();var daily=rows.Length==2&&rows.All(x=>NetBuy(x).HasValue)?rows.Sum(x=>NetBuy(x)!.Value):(decimal?)null;
                if(state=="Available")
                {
                    if(!daily.HasValue||rows.Any(x=>!x.CumulativeNetBuyMillionHkd.HasValue))state="IncompleteNetBuy";
                    else if(rows.Any(x=>Math.Abs(x.CumulativeNetBuyMillionHkd!.Value-previous[x.Channel]-NetBuy(x)!.Value)>RoundingToleranceMillionHkd))state="CumulativeDiscontinuity";
                    else
                    {
                        running+=daily.Value;
                        foreach(var row in rows)previous[row.Channel]=row.CumulativeNetBuyMillionHkd!.Value;
                    }
                }
                points.Add(new(day.Key,new DateOnly(2000,day.Key.Month,day.Key.Day).DayOfYear-1,daily/100m,state=="Available"?running/100m:null,state));
            }
            years.Add(new(year,ready?baseline!.Key:null,points));
        }
        return new(Version,Metric,first,last,through,retrieved,capture,years);
    }
    public static void ValidateObservations(IReadOnlyList<SouthboundFlowObservation> observations,DateOnly through)
    {
        if(observations.Count>10000||observations.Any(x=>x.Date>through||x.Date<SourceFrom||x.Channel is not ("002" or "004")
            ||x.BuyMillionHkd is <0 or >10000000m||x.SellMillionHkd is <0 or >10000000m||x.NetBuyMillionHkd is <-10000000m or >10000000m
            ||x.CumulativeNetBuyMillionHkd is <-1000000000m or >1000000000m)
            ||observations.Select(x=>(x.Date,x.Channel)).Distinct().Count()!=observations.Count)
            throw new InvalidDataException("南向净买入数据的日期、通道、金额或唯一性无效。");
        if(observations.Any(x=>x.BuyMillionHkd.HasValue&&x.SellMillionHkd.HasValue&&x.NetBuyMillionHkd.HasValue
            &&Math.Abs(x.BuyMillionHkd.Value-x.SellMillionHkd.Value-x.NetBuyMillionHkd.Value)>RoundingToleranceMillionHkd))
            throw new InvalidDataException("来源净买入与买入减卖出不一致，拒绝构造累计曲线。");
    }
    private static decimal? NetBuy(SouthboundFlowObservation row)=>row.BuyMillionHkd.HasValue&&row.SellMillionHkd.HasValue
        ?row.BuyMillionHkd.Value-row.SellMillionHkd.Value:row.NetBuyMillionHkd;
}
