using CRS.Plugins.NationalTeamWatch.Application;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>Compatibility facade for the shared, reviewed exchange-calendar read model.</summary>
public sealed partial class MarketRepository
{
    public IndustryPeCalendarSnapshot IndustryPeCalendar(DateOnly from,DateOnly through,DateTimeOffset cutoff)
        =>marketCalendarStore.IndustryPeCalendar(from,through,cutoff);
}
