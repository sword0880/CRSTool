using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    /// <summary>使用已核验深交所日历作公布序列的交易日对照；原件即时校验，不把周一到周五猜作交易日。</summary>
    public IndustryPeCalendarSnapshot IndustryPeCalendar(DateOnly from,DateOnly through,DateTimeOffset cutoff)
    {
        if(from>through||through.DayNumber-from.DayNumber>400||cutoff==default||cutoff>clock.GetUtcNow())throw new InvalidDataException("行业日历查询范围无效。");
        var facts=new List<(DateOnly Day,bool Open,string Fetch,string Hash,string Original,bool Valid)>();
        using(var command=Command("""
            SELECT c.day,c.is_open,c.fetch_id,f.sha256,f.original_sha256,
            f.provider='ReviewedOfficialCalendar' AND f.status='Accepted'
            AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open'),f.retrieved_utc
            FROM exchange_calendar_day c JOIN source_fetch f ON f.fetch_id=c.fetch_id WHERE c.market='SZSE' AND c.day>=$from AND c.day<=$through ORDER BY c.day
            """,("$from",Day(from)),("$through",Day(through))))
        // 既有日历表可能保存北京时间偏移，必须按时间值比较，不能与UTC字符串做字典序比较。
        using(var reader=command.ExecuteReader())while(reader.Read())facts.Add((DateOnly.ParseExact(reader.GetString(0),"yyyy-MM-dd",CultureInfo.InvariantCulture),reader.GetInt32(1)==1,reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt32(5)==1&&DateTimeOffset.Parse(reader.GetString(6),CultureInfo.InvariantCulture)<=cutoff));
        var health=raw.VerifyMany(facts.SelectMany(x=>new[]{x.Hash,x.Original}),operationCancellation);
        var days=Enumerable.Range(0,through.DayNumber-from.DayNumber+1).Select(from.AddDays).ToDictionary(x=>x,_=>(bool?)null);
        foreach(var fact in facts)if(fact.Valid&&health[fact.Hash]&&health[fact.Original])days[fact.Day]=fact.Open;
        var fingerprint=ExitAlertEvaluator.Hash(System.Text.Json.JsonSerializer.Serialize(new{Reference="SZSEReviewedCalendar",Facts=facts.Select(x=>new{x.Day,x.Open,x.Fetch,x.Hash,x.Original,x.Valid}),Days=days}));
        return new(days,fingerprint);
    }
}
