using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>Compatibility facade for reviewed exchange-calendar persistence and reads.</summary>
public sealed partial class MarketRepository
{
    private void InitializeCalendarSchema()=>marketCalendarStore.InitializeSchema();
    public void RegisterReviewedCalendar(string market,int year,byte[] content,Uri uri)=>marketCalendarStore.RegisterReviewedCalendar(market,year,content,uri);
    public bool? IsReviewedTradingDay(string market,DateOnly day)=>marketCalendarStore.IsReviewedTradingDay(market,day);
}
