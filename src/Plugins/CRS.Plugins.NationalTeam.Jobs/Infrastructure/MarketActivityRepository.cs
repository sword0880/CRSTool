using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    /// <summary>按原件和当前有效修订恢复市场/日期进度；冲突、损坏或待拒绝事实不能作为已完成跳过。</summary>
    public HashSet<(string Market,DateOnly Date)> VerifiedMarketActivityDates(DateOnly from,DateOnly to)
    {
        var rows=new List<(string Market,DateOnly Date,decimal Amount,decimal Volume,string Hash,string Original,bool Valid)>();
        using(var command=Command("""
            SELECT o.market,o.trade_date,o.amount_yuan,o.volume_shares,f.sha256,f.original_sha256,
                NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=o.fetch_id AND q.rule_id='Q15' AND q.status='Open')
            FROM market_activity_observation o JOIN source_fetch f USING(fetch_id)
            WHERE o.trade_date BETWEEN $from AND $to
                AND NOT EXISTS(SELECT 1 FROM market_activity_review r WHERE r.fetch_id=o.fetch_id AND r.decision='Rejected')
            """,("$from",from.ToString("yyyy-MM-dd")),("$to",to.ToString("yyyy-MM-dd"))))
        using(var reader=command.ExecuteReader())while(reader.Read())
            rows.Add((reader.GetString(0),DateOnly.ParseExact(reader.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(2),CultureInfo.InvariantCulture),decimal.Parse(reader.GetString(3),CultureInfo.InvariantCulture),
                reader.GetString(4),reader.GetString(5),reader.GetInt32(6)==1));
        var verified=raw.VerifyMany(rows.SelectMany(x=>new[]{x.Hash,x.Original}),operationCancellation);
        return rows.GroupBy(x=>(x.Market,x.Date)).Where(group=>group.All(x=>x.Valid&&verified[x.Hash]&&verified[x.Original])
            &&group.Select(x=>(x.Amount,x.Volume)).Distinct().Count()==1).Select(x=>x.Key).ToHashSet();
    }
    private void InitializeActivityReviewSchema()=>Execute("CREATE TABLE IF NOT EXISTS market_activity_review(fetch_id TEXT PRIMARY KEY REFERENCES source_fetch(fetch_id),decision TEXT NOT NULL,reviewer TEXT NOT NULL,reviewed_at TEXT NOT NULL); CREATE TABLE IF NOT EXISTS market_activity_review_audit(id TEXT PRIMARY KEY,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),decision TEXT NOT NULL,reviewer TEXT NOT NULL,reviewed_at TEXT NOT NULL);");
    public List<MarketActivityRevision> MarketActivityRevisions()
    {
        ValidateEvidence();var result=new List<MarketActivityRevision>();
        using var command=Command("SELECT o.fetch_id,o.market,o.trade_date,o.amount_yuan,o.volume_shares,coalesce(r.decision,'Unreviewed'),f.source_url,f.retrieved_utc FROM market_activity_observation o JOIN source_fetch f ON f.fetch_id=o.fetch_id LEFT JOIN market_activity_review r ON r.fetch_id=o.fetch_id ORDER BY o.trade_date DESC,f.retrieved_utc DESC");using var reader=command.ExecuteReader();
        while(reader.Read())result.Add(new(reader.GetString(0),reader.GetString(1),DateOnly.Parse(reader.GetString(2),CultureInfo.InvariantCulture),decimal.Parse(reader.GetString(3),CultureInfo.InvariantCulture),decimal.Parse(reader.GetString(4),CultureInfo.InvariantCulture),reader.GetString(5),reader.GetString(6),reader.GetString(7)));return result;
    }
    public void ReviewMarketActivity(string id,bool accept,string reviewer,Guid? commandId=null)
    {
        if(Committed(commandId))return;if(string.IsNullOrWhiteSpace(reviewer)||reviewer.Length>100)throw new InvalidDataException("成交统计审核人不能为空。");
        var row=MarketActivityRevisions().Single(x=>x.Id==id);
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM quality_issue WHERE evidence_id=$id AND rule_id='Q15' AND status='Open'",("$id",id)))>0)throw new InvalidDataException("原件损坏，不能接受。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        if(accept)
        {
            // 接受所选值，显式否决当前已见的其他不同值；未来新修订仍会触发冲突。
            var competing=MarketActivityRevisions().Where(x=>x.Market==row.Market&&x.Date==row.Date&&x.Id!=id&&(x.AmountYuan!=row.AmountYuan||x.VolumeShares!=row.VolumeShares)).ToArray();
            foreach(var other in competing)Execute("INSERT INTO market_activity_review VALUES($id,'Rejected',$who,$now) ON CONFLICT(fetch_id) DO UPDATE SET decision='Rejected',reviewer=excluded.reviewer,reviewed_at=excluded.reviewed_at",("$id",other.Id),("$who",reviewer),("$now",Now));
        }
        Execute("INSERT INTO market_activity_review VALUES($id,$s,$who,$now) ON CONFLICT(fetch_id) DO UPDATE SET decision=excluded.decision,reviewer=excluded.reviewer,reviewed_at=excluded.reviewed_at",("$id",id),("$s",accept?"Accepted":"Rejected"),("$who",reviewer),("$now",Now));
        Execute("INSERT INTO market_activity_review_audit VALUES($event,$id,$s,$who,$now)",("$event",Guid.NewGuid().ToString("N")),("$id",id),("$s",accept?"Accepted":"Rejected"),("$who",reviewer),("$now",Now));
        RecordCommand(commandId,"activity-review");transaction.Commit();
    }
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
        Execute("INSERT INTO market_activity_attempt VALUES($m,$d,$s) ON CONFLICT(market,trade_date) DO UPDATE SET state=excluded.state",("$m",market),("$d",date),("$s",IsReviewedTradingDay(market,day)==false?"Closed":source is null?"Failed":source.Observation is null?"NoData":"Accepted"));
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
        ValidateEvidence();return ReadMarketActivity();
    }
    private List<MarketActivityPoint> ReadMarketActivity()
    {
        var attempts=new Dictionary<(string Market,DateOnly Date),string>();
        using(var command=Command("SELECT market,trade_date,state FROM market_activity_attempt ORDER BY trade_date"))
        using(var reader=command.ExecuteReader())while(reader.Read())attempts.Add((reader.GetString(0),DateOnly.ParseExact(reader.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture)),reader.GetString(2));
        var observations=new Dictionary<(string Market,DateOnly Date),List<(decimal Amount,decimal Volume,bool Valid)>>();
        using(var command=Command("SELECT o.market,o.trade_date,o.amount_yuan,o.volume_shares,NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=o.fetch_id AND q.rule_id='Q15' AND q.status='Open') FROM market_activity_observation o WHERE NOT EXISTS(SELECT 1 FROM market_activity_review r WHERE r.fetch_id=o.fetch_id AND r.decision='Rejected')"))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            var key=(reader.GetString(0),DateOnly.ParseExact(reader.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture));
            if(!observations.TryGetValue(key,out var rows))observations.Add(key,rows=[]);
            rows.Add((decimal.Parse(reader.GetString(2),CultureInfo.InvariantCulture),decimal.Parse(reader.GetString(3),CultureInfo.InvariantCulture),reader.GetInt32(4)==1));
        }
        var points=new List<MarketActivityPoint>();
        foreach(var day in attempts.Keys.Select(x=>x.Date).Distinct().Order())
        {
            if(new[]{"SSE","SZSE"}.All(m=>IsReviewedTradingDay(m,day)==false))
            {points.Add(new(day,null,null,"Closed"));continue;}
            var values=new List<(decimal Amount,decimal Volume)>();var state="Complete";
            foreach(var market in new[]{"SSE","SZSE"})
            {
                if(!observations.TryGetValue((market,day),out var rows)){state="Incomplete";continue;}
                if(rows.Any(x=>!x.Valid)){state="Critical";continue;}
                var unique=rows.Select(x=>(x.Amount,x.Volume)).Distinct().ToArray();
                if(unique.Length!=1){state="Conflict";continue;}values.Add(unique[0]);
            }
            if(values.Count==2)points.Add(new(day,values.Sum(x=>x.Amount),values.Sum(x=>x.Volume),"Complete"));
            else points.Add(new(day,null,null,new[]{"SSE","SZSE"}.All(m=>attempts.GetValueOrDefault((m,day))=="NoData") && !observations.Keys.Any(x=>x.Date==day) && !new[]{"SSE","SZSE"}.Any(m=>IsReviewedTradingDay(m,day)==true)?"NoData":state));
        }
        return MarketActivityMetrics.Calculate(points);
    }
}
