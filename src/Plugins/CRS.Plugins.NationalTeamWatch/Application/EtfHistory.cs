using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed record EtfHistoryInput(EtfKey Key,IReadOnlyList<DateOnly> Calendar,IReadOnlyList<SharePoint> Points);
public sealed record EtfHistoryPoint(DateOnly Date,decimal? Shares,decimal? DeltaShares,int Covered,int Total);

/// <summary>固定当前监控篮子逐日汇总；不跨缺口、不将缺失填零、不归因机构交易。</summary>
public static class EtfHistory
{
    public static List<EtfHistoryPoint> Calculate(IReadOnlyList<EtfHistoryInput> inputs)
    {
        if(inputs.Count==0)return [];
        if(inputs.Select(x=>x.Key).Distinct().Count()!=inputs.Count)throw new ArgumentException("重复ETF。");
        var maps=inputs.Select(x=>x.Points.ToDictionary(p=>p.Date)).ToArray();
        var days=inputs.SelectMany(x=>x.Calendar.Concat(x.Points.Select(p=>p.Date))).Distinct().Order();
        return days.Select(day=>
        {
            var covered=maps.Count(x=>x.ContainsKey(day));
            decimal? total=covered==inputs.Count?maps.Sum(x=>x[day].Shares):null;
            // 每个市场必须确认相同的前一交易日，避免把不同期间的变化相加。
            var predecessors=inputs.Select(x=>x.Calendar.Where(d=>d<day).Order().LastOrDefault()).Distinct().ToArray();
            var deltas=inputs.Select(x=>EtfMetrics.Calculate(day,1,x.Calendar,x.Points).DeltaShares).ToArray();
            decimal? delta=predecessors.Length==1 && deltas.All(x=>x.HasValue)?deltas.Sum(x=>x!.Value):null;
            return new EtfHistoryPoint(day,total,delta,covered,inputs.Count);
        }).ToList();
    }
}
