using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    private void InitializeCalendarSchema()=>Execute("CREATE TABLE IF NOT EXISTS exchange_calendar_day(market TEXT NOT NULL,day TEXT NOT NULL,is_open INTEGER NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),PRIMARY KEY(market,day));");
    public void RegisterReviewedCalendar(string market,int year,byte[] content,Uri uri)
    {
        if(uri!=ReviewedExchangeCalendar.Source(market,year))throw new InvalidDataException("日历来源不匹配。");
        var days=ReviewedExchangeCalendar.Parse(market,year,System.Text.Encoding.UTF8.GetString(content));
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        var hash=raw.Save(content);
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM exchange_calendar_day c JOIN source_fetch f ON f.fetch_id=c.fetch_id WHERE c.market=$m AND c.day LIKE $y AND f.sha256=$h",("$m",market),("$y",$"{year}-%"),("$h",hash)))==days.Count){transaction.Commit();return;}
        var fetch=RecordFetch(hash,uri.AbsoluteUri,hash,"ReviewedOfficialCalendar","calendar-2026-v1");
        foreach(var (date,open) in days)
        {
            var d=date.ToString("yyyy-MM-dd");
            Execute("INSERT INTO exchange_calendar_day VALUES($m,$d,$o,$f) ON CONFLICT(market,day) DO UPDATE SET is_open=excluded.is_open,fetch_id=excluded.fetch_id",("$m",market),("$d",d),("$o",open?1:0),("$f",fetch));
            if(open)Execute("INSERT INTO trading_day VALUES($m,$d,$f) ON CONFLICT(market,trade_date) DO UPDATE SET fetch_id=excluded.fetch_id",("$m",market),("$d",d),("$f",fetch));
            else Execute("DELETE FROM trading_day WHERE market=$m AND trade_date=$d",("$m",market),("$d",d));
        }
        Execute("UPDATE source_fetch SET status='Accepted' WHERE fetch_id=$f",("$f",fetch));transaction.Commit();
    }
    public bool? IsReviewedTradingDay(string market,DateOnly day)
    {
        var value=Scalar("SELECT c.is_open FROM exchange_calendar_day c WHERE market=$m AND day=$d AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=c.fetch_id AND q.rule_id='Q15' AND q.status='Open')",("$m",market),("$d",day.ToString("yyyy-MM-dd")));
        return value is null?null:Convert.ToInt32(value)==1;
    }
}
