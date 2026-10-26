using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>
/// Shared, reviewed exchange-calendar read model consumed by industry PE and index-series research.
/// Does not infer weekdays as trading days and verifies original evidence on every query.
/// </summary>
internal sealed class MarketCalendarStore
{
    private readonly IMarketRepositorySession _session;
    public MarketCalendarStore(IMarketRepositorySession session)=>_session=session;

    public void InitializeSchema()
        =>_session.Execute("CREATE TABLE IF NOT EXISTS exchange_calendar_day(market TEXT NOT NULL,day TEXT NOT NULL,is_open INTEGER NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),PRIMARY KEY(market,day));");

    public void RegisterReviewedCalendar(string market,int year,byte[] content,Uri uri)
    {
        if(uri!=ReviewedExchangeCalendar.Source(market,year))throw new InvalidDataException("日历来源不匹配。");
        var days=ReviewedExchangeCalendar.Parse(market,year,System.Text.Encoding.UTF8.GetString(content));
        _session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();
        var hash=_session.RawEvidence.Save(content);
        if(Convert.ToInt32(_session.Scalar("SELECT count(*) FROM exchange_calendar_day c JOIN source_fetch f ON f.fetch_id=c.fetch_id WHERE c.market=$m AND c.day LIKE $y AND f.sha256=$h",("$m",market),("$y",$"{year}-%"),("$h",hash)))==days.Count)
        {transaction.Commit();return;}
        var fetch=_session.RecordFetch(hash,uri.AbsoluteUri,hash,"ReviewedOfficialCalendar",$"calendar-{year}-v1");
        foreach(var (date,open) in days)
        {
            var d=date.ToString("yyyy-MM-dd");
            _session.Execute("INSERT INTO exchange_calendar_day VALUES($m,$d,$o,$f) ON CONFLICT(market,day) DO UPDATE SET is_open=excluded.is_open,fetch_id=excluded.fetch_id",("$m",market),("$d",d),("$o",open?1:0),("$f",fetch));
            if(open)_session.Execute("INSERT INTO trading_day VALUES($m,$d,$f) ON CONFLICT(market,trade_date) DO UPDATE SET fetch_id=excluded.fetch_id",("$m",market),("$d",d),("$f",fetch));
            else _session.Execute("DELETE FROM trading_day WHERE market=$m AND trade_date=$d",("$m",market),("$d",d));
        }
        _session.Execute("UPDATE source_fetch SET status='Accepted' WHERE fetch_id=$f",("$f",fetch));transaction.Commit();
    }

    public bool? IsReviewedTradingDay(string market,DateOnly day)
    {
        var value=_session.Scalar("SELECT c.is_open FROM exchange_calendar_day c WHERE market=$m AND day=$d AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=c.fetch_id AND q.rule_id='Q15' AND q.status='Open')",("$m",market),("$d",day.ToString("yyyy-MM-dd")));
        return value is null?null:Convert.ToInt32(value)==1;
    }

    public IndustryPeCalendarSnapshot IndustryPeCalendar(DateOnly from,DateOnly through,DateTimeOffset cutoff)
    {
        var snapshot=ReadPublishedTradingCalendar(from,through,cutoff);
        return new(snapshot.Days,snapshot.Fingerprint);
    }

    public (IReadOnlyDictionary<DateOnly,bool?> Days,string Fingerprint) ReadPublishedTradingCalendar(DateOnly from,DateOnly through,DateTimeOffset cutoff)
    {
        if(from>through||through.DayNumber-from.DayNumber>400||cutoff==default||cutoff>_session.Clock.GetUtcNow())
            throw new InvalidDataException("行业日历查询范围无效。");
        var facts=new List<(DateOnly Day,bool Open,string Fetch,string Hash,string Original,bool Valid)>();
        using(var command=_session.Command("""
            SELECT c.day,c.is_open,c.fetch_id,f.sha256,f.original_sha256,
            f.provider='ReviewedOfficialCalendar' AND f.status='Accepted'
            AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open'),f.retrieved_utc
            FROM exchange_calendar_day c JOIN source_fetch f ON f.fetch_id=c.fetch_id WHERE c.market='SZSE' AND c.day>=$from AND c.day<=$through ORDER BY c.day
            """,("$from",Day(from)),("$through",Day(through))))
        // 既有日历表可能保存北京时间偏移，必须按时间值比较，不能与UTC字符串做字典序比较。
        using(var reader=command.ExecuteReader())while(reader.Read())
            facts.Add((DateOnly.ParseExact(reader.GetString(0),"yyyy-MM-dd",CultureInfo.InvariantCulture),
                reader.GetInt32(1)==1,reader.GetString(2),reader.GetString(3),reader.GetString(4),
                reader.GetInt32(5)==1&&DateTimeOffset.Parse(reader.GetString(6),CultureInfo.InvariantCulture)<=cutoff));
        var health=_session.RawEvidence.VerifyMany(facts.SelectMany(x=>new[]{x.Hash,x.Original}),_session.OperationCancellation);
        var days=Enumerable.Range(0,through.DayNumber-from.DayNumber+1).Select(from.AddDays).ToDictionary(x=>x,_=>(bool?)null);
        foreach(var fact in facts)if(fact.Valid&&health[fact.Hash]&&health[fact.Original])days[fact.Day]=fact.Open;
        var fingerprint=ExitAlertEvaluator.Hash(JsonSerializer.Serialize(new
        {
            Reference="SZSEReviewedCalendar",
            Facts=facts.Select(x=>new{x.Day,x.Open,x.Fetch,x.Hash,x.Original,x.Valid}),
            Days=days
        }));
        return(days,fingerprint);
    }

    private static string Day(DateOnly day)=>day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
}
