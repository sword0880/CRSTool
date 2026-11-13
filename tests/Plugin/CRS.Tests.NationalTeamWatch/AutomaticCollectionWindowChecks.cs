using CRS.Plugins.NationalTeamWatch.Domain;

internal static class AutomaticCollectionWindowChecks
{
    internal static void Run()
    {
        var day=new DateOnly(2026,11,6);
        void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS 自动发布窗口："+text);}
        var noon=new DateTimeOffset(2026,11,6,13,0,0,TimeSpan.FromHours(8));
        Check(AutomaticCollectionWindow.EtfShares(noon)==day.AddDays(-1)&&AutomaticCollectionWindow.MarketActivity(noon)==day.AddDays(-1),"盘中自动采集不请求尚未进入日终窗口的当日数据");
        var shares=new DateTimeOffset(2026,11,6,23,15,0,TimeSpan.FromHours(8));
        Check(AutomaticCollectionWindow.EtfShares(shares.AddTicks(-1))==day.AddDays(-1)&&AutomaticCollectionWindow.EtfShares(shares)==day,"ETF份额23:15边界包含当日");
        var activity=shares.AddMinutes(15);
        Check(AutomaticCollectionWindow.MarketActivity(activity.AddTicks(-1))==day.AddDays(-1)&&AutomaticCollectionWindow.MarketActivity(activity)==day,"成交统计23:30边界包含当日");
        Check(AutomaticCollectionWindow.MarketActivity(activity.ToUniversalTime())==day,"先转换北京时间，不依赖调用者偏移");
        Check(AutomaticCollectionWindow.EtfShares(new(2027,1,1,0,1,0,TimeSpan.FromHours(8)))==new DateOnly(2026,12,31),"跨年窗口回退上一日，不包含未来日期");
    }
}
