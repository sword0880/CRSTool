using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public DateOnly? ReadMarketActivityCursor()=>Scalar("SELECT checked_through FROM market_activity_cursor WHERE id=1") is string date?DateOnly.ParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture):null;
    public void RecordMarketActivityCursor(DateOnly day)
    {
        beforeWrite?.Invoke();Execute("INSERT INTO market_activity_cursor VALUES(1,$d) ON CONFLICT(id) DO UPDATE SET checked_through=max(checked_through,excluded.checked_through)",("$d",day.ToString("yyyy-MM-dd")));
    }
    public void SaveMarketActivity(string market,DateOnly day,MarketActivitySource? source)
    {
        if(market is not ("SSE" or "SZSE") || source is not null && (source.Market!=market || source.Date!=day || source.Observation is {} o && (o.Market!=market || o.Date!=day || o.AmountYuan<0 || o.VolumeShares<0)))throw new InvalidDataException("成交统计标识或数值异常。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        var date=day.ToString("yyyy-MM-dd");
        Execute("INSERT INTO market_activity_attempt VALUES($m,$d,$s) ON CONFLICT(market,trade_date) DO UPDATE SET state=excluded.state",("$m",market),("$d",date),("$s",source is null?"Failed":source.Observation is null?"NoData":"Accepted"));
        if(source is not null)
        {
            var hash=raw.Save(source.Content);
            // 同一原件重复采集不追加事实；不同值保留全部修订，查询时阻断冲突日。
            if(source.Observation is {} value && Convert.ToInt32(Scalar("SELECT count(*) FROM market_activity_observation o JOIN source_fetch f USING(fetch_id) WHERE o.market=$m AND o.trade_date=$d AND f.sha256=$h",("$m",market),("$d",date),("$h",hash)))==0)
            {
                var fetch=RecordFetch(hash,source.Uri.AbsoluteUri,hash,"OfficialMarketActivity","market-activity-v1");
                Execute("INSERT INTO market_activity_observation VALUES($m,$d,$a,$v,$f)",("$m",market),("$d",date),("$a",Decimal(value.AmountYuan)),("$v",Decimal(value.VolumeShares)),("$f",fetch));
                Execute("UPDATE source_fetch SET status='Accepted' WHERE fetch_id=$f",("$f",fetch));
            }
        }
        transaction.Commit();
    }
    public List<MarketActivityPoint> MarketActivity()
    {
        ValidateEvidence();
        var attempts=new Dictionary<(string Market,DateOnly Date),string>();
        using(var command=Command("SELECT market,trade_date,state FROM market_activity_attempt ORDER BY trade_date"))
        using(var reader=command.ExecuteReader())while(reader.Read())attempts.Add((reader.GetString(0),DateOnly.ParseExact(reader.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture)),reader.GetString(2));
        var observations=new Dictionary<(string Market,DateOnly Date),List<(decimal Amount,decimal Volume,bool Valid)>>();
        using(var command=Command("SELECT o.market,o.trade_date,o.amount_yuan,o.volume_shares,NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=o.fetch_id AND q.rule_id='Q15' AND q.status='Open') FROM market_activity_observation o"))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            var key=(reader.GetString(0),DateOnly.ParseExact(reader.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture));
            if(!observations.TryGetValue(key,out var rows))observations.Add(key,rows=[]);
            rows.Add((decimal.Parse(reader.GetString(2),CultureInfo.InvariantCulture),decimal.Parse(reader.GetString(3),CultureInfo.InvariantCulture),reader.GetInt32(4)==1));
        }
        var points=new List<MarketActivityPoint>();
        foreach(var day in attempts.Keys.Select(x=>x.Date).Distinct().Order())
        {
            var values=new List<(decimal Amount,decimal Volume)>();var state="Complete";
            foreach(var market in new[]{"SSE","SZSE"})
            {
                if(!observations.TryGetValue((market,day),out var rows)){state="Incomplete";continue;}
                if(rows.Any(x=>!x.Valid)){state="Critical";continue;}
                var unique=rows.Select(x=>(x.Amount,x.Volume)).Distinct().ToArray();
                if(unique.Length!=1){state="Conflict";continue;}values.Add(unique[0]);
            }
            if(values.Count==2)points.Add(new(day,values.Sum(x=>x.Amount),values.Sum(x=>x.Volume),"Complete"));
            else points.Add(new(day,null,null,new[]{"SSE","SZSE"}.All(m=>attempts.GetValueOrDefault((m,day))=="NoData") && !observations.Keys.Any(x=>x.Date==day)?"NoData":state));
        }
        return MarketActivityMetrics.Calculate(points);
    }
}
