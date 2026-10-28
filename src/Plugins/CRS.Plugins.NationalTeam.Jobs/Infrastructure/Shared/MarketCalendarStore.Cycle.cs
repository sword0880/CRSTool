using System.Globalization;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class MarketCalendarStore
{
    internal sealed record CycleCalendarSnapshot(IReadOnlyDictionary<DateOnly,bool?> Days,string Fingerprint,IReadOnlyList<MarketCycleSourceReference> Sources);
    public CycleCalendarSnapshot CycleCalendar(DateOnly from,DateOnly through,DateTimeOffset cutoff,string market)
    {
        var snapshot=ReadPublishedTradingCalendar(from,through,cutoff,market);
        var sources=new List<MarketCycleSourceReference>();var originals=new Dictionary<string,IReadOnlyDictionary<DateOnly,bool>>();
        var verified=new Dictionary<DateOnly,bool?>(snapshot.Days);
        using var command=_session.Command("""
            SELECT c.day,c.fetch_id,f.sha256,f.original_sha256,f.source_url,f.retrieved_utc,f.parser_version
            FROM exchange_calendar_day c JOIN source_fetch f ON f.fetch_id=c.fetch_id
            WHERE c.market=$market AND c.day>=$from AND c.day<=$through ORDER BY c.day
            """,("$market",market),("$from",Day(from)),("$through",Day(through)));
        using var reader=command.ExecuteReader();
        while(reader.Read())
        {
            _session.OperationCancellation.ThrowIfCancellationRequested();var day=DateOnly.ParseExact(reader.GetString(0),"yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(verified[day] is null)continue;
            var id=reader.GetString(1);var hash=reader.GetString(2);var original=reader.GetString(3);var url=reader.GetString(4);var at=DateTimeOffset.Parse(reader.GetString(5),CultureInfo.InvariantCulture);
            if(!originals.TryGetValue(id,out var parsed))
            {
                try
                {
                    if(hash!=original||url!=ReviewedExchangeCalendar.Source(market,day.Year).AbsoluteUri||reader.GetString(6)!=$"calendar-{day.Year}-v1")throw new InvalidDataException("日历来源身份不符。");
                    parsed=ReviewedExchangeCalendar.Parse(market,day.Year,Encoding.UTF8.GetString(_session.RawEvidence.ReadVerified(original,_session.OperationCancellation))).ToDictionary(x=>x.Date,x=>x.Open);
                }
                catch(InvalidDataException){parsed=new Dictionary<DateOnly,bool>();}
                originals.Add(id,parsed);sources.Add(new("Calendar",day,id,market+"AnnualNotice",url,hash,original,at,at,at,parsed.Count>0?"Accepted":"Critical"));
            }
            if(!parsed.TryGetValue(day,out var open)||verified[day]!=open)verified[day]=null;
        }
        return new(verified,ExitAlertEvaluator.Hash(JsonSerializer.Serialize(new{Version="cycle-calendar-original-reparse-v1",snapshot.Fingerprint,Days=verified,Sources=sources})),sources);
    }
}
