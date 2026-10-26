using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>股票成交统计与已核验日期读取。</summary>
public interface IMarketActivityReadRepository
{
    List<MarketActivityPoint> MarketActivity();
    HashSet<(string Market,DateOnly Date)> VerifiedMarketActivityDates(DateOnly from,DateOnly to);
    DateOnly? ReadMarketActivityCursor();
    List<MarketActivityRevision> MarketActivityRevisions();
}
