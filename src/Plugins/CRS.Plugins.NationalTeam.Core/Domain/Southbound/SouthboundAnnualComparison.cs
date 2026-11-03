namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>来源公布的百万港元金额；任一路缺失时不构造南向合计。</summary>
public sealed record SouthboundBuyObservation(DateOnly Date,string Channel,decimal? BuyMillionHkd);
public sealed record SouthboundBuyPoint(DateOnly Date,int CalendarDay,decimal? BuyYiHkd);
public sealed record SouthboundYearCurve(int Year,IReadOnlyList<SouthboundBuyPoint> Points);
public sealed record SouthboundAnnualReport(int FirstYear,int LastYear,DateOnly Through,DateTimeOffset? RetrievedAt,
    string? CaptureId,IReadOnlyList<SouthboundYearCurve> Years)
{
    public string Basis=>"南向买入成交金额（港股通沪＋港股通深），单位：亿港元；东方财富整理数据，不代表净买入或整个港股市场资金流。";
}

/// <summary>各年按相同月日排列；采用闰年坐标，三月以后不因闰日错位，不补造休市或未来数据。</summary>
public static class SouthboundAnnualComparison
{
    public const int EarliestYear=2017;
    public static void ValidateYears(int first,int last,int currentYear)
    {
        if(first<EarliestYear||last<first||last>currentYear||last-first>9)
            throw new InvalidDataException("请选择2017年以来、最多十个年度的有效区间。");
    }
    public static SouthboundAnnualReport Build(int first,int last,DateOnly through,DateTimeOffset? retrieved,
        string? capture,IReadOnlyList<SouthboundBuyObservation> observations)
    {
        ValidateYears(first,last,through.Year);
        if(observations.Count>10000||observations.Any(x=>x.Date>through||x.Date.Year<EarliestYear||x.Channel is not ("002" or "004")||x.BuyMillionHkd<0)
            ||observations.Select(x=>(x.Date,x.Channel)).Distinct().Count()!=observations.Count)
            throw new InvalidDataException("南向买入数据的日期、通道、金额或唯一性无效。");
        var points=observations.Where(x=>x.Date.Year>=first&&x.Date.Year<=last).GroupBy(x=>x.Date).OrderBy(x=>x.Key)
            .Select(g=>new SouthboundBuyPoint(g.Key,new DateOnly(2000,g.Key.Month,g.Key.Day).DayOfYear-1,
                g.Count()==2&&g.All(x=>x.BuyMillionHkd.HasValue)?g.Sum(x=>x.BuyMillionHkd!.Value)/100m:null)).ToArray();
        return new(first,last,through,retrieved,capture,Enumerable.Range(first,last-first+1)
            .Select(y=>new SouthboundYearCurve(y,points.Where(p=>p.Date.Year==y).ToArray())).ToArray());
    }
}
