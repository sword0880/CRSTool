namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed record MarketActivityObservation(string Market,DateOnly Date,decimal AmountYuan,decimal VolumeShares);
public sealed record MarketActivitySource(Uri Uri,byte[] Content,string Market,DateOnly Date,MarketActivityObservation? Observation);
public interface IMarketActivitySource
{
    Task<MarketActivitySource> FetchAsync(string market,DateOnly day,CancellationToken token);
}
public sealed record MarketActivityPoint(DateOnly Date,decimal? AmountYuan,decimal? VolumeShares,string State,
    decimal? Mean5=null,decimal? Mean20=null,decimal? ActivityRatio=null);

/// <summary>比较前N条完整收盘记录，不冒充已核验连续交易日；已知故障或单市场缺口重置窗口。</summary>
public static class MarketActivityMetrics
{
    public static List<MarketActivityPoint> Calculate(IReadOnlyList<MarketActivityPoint> points)
    {
        var result=new List<MarketActivityPoint>();var previous=new Queue<decimal>();
        foreach(var point in points.OrderBy(x=>x.Date))
        {
            if(point.State!="Complete" || point.AmountYuan is null || point.VolumeShares is null)
            {
                if(point.State!="NoData")previous.Clear();
                result.Add(point with{Mean5=null,Mean20=null,ActivityRatio=null});continue;
            }
            decimal? Mean(int count)=>previous.Count>=count?previous.TakeLast(count).Average():null;
            var mean20=Mean(20);
            result.Add(point with{Mean5=Mean(5),Mean20=mean20,ActivityRatio=mean20>0?point.AmountYuan/mean20:null});
            previous.Enqueue(point.AmountYuan.Value);if(previous.Count>20)previous.Dequeue();
        }
        return result;
    }
}
