using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
public sealed partial class MarketRepository
{
    /// <summary>补采250日不等于实际连续采集20日；按每个交易日的真实接收时点独立核验。</summary>
    public List<EtfAcceptance> Acceptance(DateOnly through)
    {
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        if(through>=today)throw new InvalidDataException("验收只统计已结束的自然日，不能提前批准当日采集。");
        ValidateEvidence();var result=new List<EtfAcceptance>();
        foreach(var item in Configuration().Where(x=>x.Active))
        {
            var key=new EtfKey(item.Market,item.Ticker);var calendar=ReadCalendar(key.Market);
            var days=calendar.Where(x=>x.Date<=through).TakeLast(250).ToArray();
            var start=days.Length==0?(DateOnly?)null:days[0].Date;
            var verified=days.Length>0&&days.All(x=>x.Valid)&&Enumerable.Range(start!.Value.DayNumber,through.DayNumber-start.Value.DayNumber+1).All(n=>IsReviewedTradingDay(key.Market,DateOnly.FromDayNumber(n)).HasValue);
            var points=ReadPoints(key).Select(x=>x.Date).ToHashSet();var accepted=days.Count(x=>points.Contains(x.Date));var continuous=0;
            var recent=days.TakeLast(20).Reverse().ToArray();
            foreach(var day in recent)
            {
                // 次日迟发允许；同一天回填20个历史日期不能冒充20日持续采集。
                var attempts=CollectionAttempts(key,day.Date,day.Date);
                if(!points.Contains(day.Date)||!attempts.Any(a=>a.State=="Accepted"&&a.OriginalHash is {} hash&&raw.Verify(hash)&&WithinDailyWindow(a.UpdatedAt,day.Date)))break;
                continuous++;
            }
            result.Add(new(key.Market,key.Ticker,through,start,days.Length,accepted,days.Length-accepted,continuous,verified));
        }
        return result;
    }
    private static bool WithinDailyWindow(string updatedAt,DateOnly day)
    {
        var local=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.Parse(updatedAt,CultureInfo.InvariantCulture),"China Standard Time");
        var received=DateOnly.FromDateTime(local.DateTime);
        return received==day||received==day.AddDays(1);
    }
}
