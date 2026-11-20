namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>自动近段采集沿用日终计划窗口；不是来源必定公布的承诺，手动指定日期仍可独立核对。</summary>
public static class AutomaticCollectionWindow
{
    public static DateOnly EtfShares(DateTimeOffset instant)=>Through(instant,23,15);
    public static DateOnly MarketActivity(DateTimeOffset instant)=>Through(instant,23,30);
    private static DateOnly Through(DateTimeOffset instant,int hour,int minute)
    {
        var now=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(instant,CRS.Timing.BeijingClock.ZoneId);
        var day=DateOnly.FromDateTime(now.DateTime);
        return now.TimeOfDay<new TimeSpan(hour,minute,0)?day.AddDays(-1):day;
    }
}
