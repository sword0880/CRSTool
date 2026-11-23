namespace CRS.Domain.Performance;

public sealed record PerformanceCurvePoint(DateOnly Date,decimal? NetAssets,decimal? UnitValue,decimal? BenchmarkValue);
public sealed record PerformanceCurve(PerformanceCurvePoint[] Points,string Benchmark,string Explanation);

/// <summary>使用同一绩效引擎计算净资产和扣除资金流影响的累计净值，指数使用同日价格和汇率。</summary>
public static class PerformanceCurveCalculator
{
    public static PerformanceCurve Calculate(PerformanceData data,PerformanceResult result,string benchmark,CancellationToken cancellation=default)
    {
        var request=result.Request with{From=result.EffectiveFrom,SinceInception=false};
        var dates=data.Valuations.Where(v=>request.AccountIds.Contains(v.AccountId) && v.Moment==ValuationMoment.EndOfDay).Select(v=>v.Date)
            .Concat(data.Prices.Where(p=>p.Market=="INDEX" && p.Security==benchmark).Select(p=>p.Date))
            .Append(request.From).Append(request.To).Where(d=>d>=request.From && d<=request.To).Distinct().Order().ToArray();
        // 有界采样；不补齐缺失净值，不把净入金误当成收益。
        var step=Math.Max(1,(int)Math.Ceiling(dates.Length/160d));
        dates=dates.Where((_,i)=>i%step==0 || i==dates.Length-1).ToArray();
        decimal? Quote(DateOnly day)
        {
            var available=data.Prices.Where(p=>p.Market=="INDEX" && p.Security==benchmark && p.Date<=day && p.Date>=day.AddDays(-7) && p.Quality!=PerformanceQuality.InsufficientData).ToArray();
            var quotes=available.Where(p=>p.Date==available.Select(x=>(DateOnly?)x.Date).Max()).ToArray();
            if(quotes.Length==0 || quotes.Any(p=>p.Price!=quotes[0].Price || p.Currency!=quotes[0].Currency))return null;
            var quote=quotes[0];if(quote.Currency==request.Currency)return quote.Price;
            decimal? Rate(string a,string b)
            {
                var online=data.Prices.Where(p=>p.Market=="INDEX_FX" && p.Security==a+b && p.Id.StartsWith("Yahoo:FX:"+benchmark+":",StringComparison.Ordinal) && p.Date<=day && p.Date>=day.AddDays(-7)).ToArray();
                if(online.Length>0){var latest=online.Max(p=>p.Date);var rates=online.Where(p=>p.Date==latest).Select(p=>p.Price).Distinct().ToArray();return rates.Length==1?rates[0]:null;}
                var local=data.Fx.Where(f=>f.Date==day && f.From==a && f.To==b && f.Quality!=PerformanceQuality.InsufficientData).Select(f=>f.Rate).Distinct().ToArray();return local.Length==1?local[0]:null;
            }
            var fx=Rate(quote.Currency,request.Currency);var inverse=Rate(request.Currency,quote.Currency);
            if(fx is null && inverse>0)fx=1/inverse;
            return fx is null?null:quote.Price*fx;
        }
        var baseline=Quote(request.From);var opening=result.Metrics.Single(m=>m.Id=="OpeningNav").Value;
        var points=new List<PerformanceCurvePoint>();
        foreach(var date in dates)
        {
            cancellation.ThrowIfCancellationRequested();
            decimal? nav,unit;
            if(date==request.From){nav=opening;unit=opening>0?100m:null;}
            else if(date!=request.To && !request.AccountIds.All(id=>data.Valuations.Any(v=>v.AccountId==id && v.Date==date && v.Moment==ValuationMoment.EndOfDay)))
            {nav=null;unit=null;}
            else
            {
                var calculated=date==request.To?result:PerformanceCalculator.Calculate(data,request with{To=date},cancellation);
                nav=calculated.Metrics.Single(m=>m.Id=="EndingNav").Value;
                var twr=calculated.Metrics.Single(m=>m.Id=="Twr").Value;unit=twr.HasValue?100m*(1+twr.Value):null;
            }
            var price=Quote(date);points.Add(new(date,nav,unit,baseline>0 && price.HasValue?price/baseline*100:null));
        }
        return new(points.ToArray(),benchmark,"净资产按报告币种展示；比较曲线起点为 100，组合使用扣除外部资金流影响的 TWR，指数为同币种价格回报（不含分红）。指数和对比用汇率在非交易日沿用最近 7 天内报价；组合曲线仅连接有完整估值的日期，不生成缺失日期的数据。最多约 161 个采样点。行情来源 Yahoo Finance，保留每次响应。");
    }
}
