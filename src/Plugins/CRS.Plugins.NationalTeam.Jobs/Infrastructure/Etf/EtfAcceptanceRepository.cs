using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
public sealed partial class MarketRepository:IEtfAcceptanceProfileRepository
{
    public EtfAcceptanceQueryProfile ProfileAcceptance(EtfKey? key,DateOnly through)
    {
        var profile=ProfileQuery(()=>ReadAcceptance(through,key));
        return new(profile.Result,profile.Stages);
    }
    /// <summary>补采250日不等于实际连续采集20日；按每个交易日的真实接收时点独立核验。</summary>
    public List<EtfAcceptance> Acceptance(DateOnly through)=>ReadAcceptance(through,null);
    public List<EtfAcceptance> Acceptance(EtfKey key,DateOnly through)=>ReadAcceptance(through,key);
    private List<EtfAcceptance> ReadAcceptance(DateOnly through,EtfKey? selected)
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(clock).DateTime);
        if(through>=today)throw new InvalidDataException("验收只统计已结束的自然日，不能提前批准当日采集。");
        // 完整性范围沿用既有规则；只复用本次核验结果，下一次查询/发送前仍重新读原件。
        var verifiedEvidence=new Dictionary<string,bool>(ValidateEvidence(selected,includeCalendar:true),StringComparer.Ordinal);
        var result=new List<EtfAcceptance>();
        var started=System.Diagnostics.Stopwatch.GetTimestamp();
        var active=Configuration().Where(x=>x.Active&&(selected is null||x.Market==selected.Market&&x.Ticker==selected.Ticker)).ToArray();
        // 同市场日历只读一次；用有界区间计数校验完整自然日，不对每只ETF逐日发起数百次SQL。
        var calendars=active.Select(x=>x.Market).Distinct().ToDictionary(m=>m,m=>ReadCalendar(m).Where(x=>x.Date<=through).TakeLast(250).ToArray());
        var verifiedMarkets=calendars.ToDictionary(x=>x.Key,x=>x.Value.Length>0&&x.Value.All(d=>d.Valid)
            &&Convert.ToInt32(Scalar("""
                SELECT count(*) FROM exchange_calendar_day c WHERE c.market=$m AND c.day BETWEEN $from AND $to
                AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=c.fetch_id AND q.rule_id='Q15' AND q.status='Open')
                """,("$m",x.Key),("$from",x.Value[0].Date.ToString("yyyy-MM-dd")),("$to",through.ToString("yyyy-MM-dd"))))==through.DayNumber-x.Value[0].Date.DayNumber+1);
        RecordQueryStage("AcceptanceCalendarAndCoverageSql",started,calendars.Sum(x=>x.Value.Length));started=System.Diagnostics.Stopwatch.GetTimestamp();
        var allPoints=ReadDashboardPoints(active.Select(x=>new EtfKey(x.Market,x.Ticker)));
        RecordQueryStage("AcceptancePointsSql",started,allPoints.Sum(x=>x.Value.Count));started=System.Diagnostics.Stopwatch.GetTimestamp();
        var receipts=ReadAcceptanceReceipts(active.Select(x=>new EtfKey(x.Market,x.Ticker)),calendars);
        RecordQueryStage("AcceptanceReceiptsSql",started,receipts.Values.Sum(x=>x.Count));started=System.Diagnostics.Stopwatch.GetTimestamp();
        var additionalMs=0d;var additionalReads=0;
        bool Verified(string hash)
        {
            if(verifiedEvidence.TryGetValue(hash,out var verified))return verified;
            var reading=System.Diagnostics.Stopwatch.GetTimestamp();
            verified=raw.VerifyMany([hash],operationCancellation)[hash];
            additionalMs+=System.Diagnostics.Stopwatch.GetElapsedTime(reading).TotalMilliseconds;additionalReads++;
            verifiedEvidence.Add(hash,verified);return verified;
        }
        foreach(var item in active)
        {
            var key=new EtfKey(item.Market,item.Ticker);var days=calendars[key.Market];
            var start=days.Length==0?(DateOnly?)null:days[0].Date;
            var verified=verifiedMarkets[key.Market];
            operationCancellation.ThrowIfCancellationRequested();
            var points=allPoints[key].Select(x=>x.Date).ToHashSet();var accepted=days.Count(x=>points.Contains(x.Date));var continuous=0;
            var recent=days.TakeLast(20).Reverse().ToArray();
            foreach(var day in recent)
            {
                // 次日迟发允许；同一天回填20个历史日期不能冒充20日持续采集。
                if(!points.Contains(day.Date)||!receipts.TryGetValue((key,day.Date),out var attempts)
                    ||!attempts.Any(a=>Verified(a.Hash)&&WithinDailyWindow(a.AcceptedAt,day.Date)&&WithinDailyWindow(a.ReceivedAt,day.Date)))break;
                continuous++;
            }
            result.Add(new(key.Market,key.Ticker,through,start,days.Length,accepted,days.Length-accepted,continuous,verified));
        }
        RecordQueryMeasurement("AcceptanceAdditionalVerification",additionalMs,additionalReads);
        RecordQueryMeasurement("AcceptanceProjection",System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds-additionalMs,result.Count);return result;
    }
    /// <summary>以北京时间验收真实接收窗口；历史回填不计入连续运行天数。</summary>
    private static bool WithinDailyWindow(string updatedAt,DateOnly day)
    {
        var local=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.Parse(updatedAt,CultureInfo.InvariantCulture),"China Standard Time");
        var received=DateOnly.FromDateTime(local.DateTime);
        return received==day||received==day.AddDays(1);
    }
}
