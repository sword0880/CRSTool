// 交易日历仓储的窄接口转发，日历持久化及核验由对应Store负责，聚合入口不重复实现。
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>Compatibility facade for reviewed exchange-calendar persistence and reads.</summary>
public sealed partial class MarketRepository
{
    public void RegisterReviewedCalendar(string market,int year,byte[] content,Uri uri)=>marketCalendarStore.RegisterReviewedCalendar(market,year,content,uri);
    public bool? IsReviewedTradingDay(string market,DateOnly day)=>marketCalendarStore.IsReviewedTradingDay(market,day);
}
