using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>Compatibility facade for market turnover/volume persistence and review.</summary>
public sealed partial class MarketRepository
{
    public HashSet<(string Market,DateOnly Date)> VerifiedMarketActivityDates(DateOnly from,DateOnly to)=>marketActivityStore.VerifiedMarketActivityDates(from,to);
    public List<MarketActivityRevision> MarketActivityRevisions()=>marketActivityStore.MarketActivityRevisions();
    public MarketActivityKnowledgeReport MarketActivityKnowledge(DateOnly from,DateOnly through,DateTimeOffset? cutoff)=>marketActivityStore.MarketActivityKnowledge(from,through,cutoff);
    public void ReviewMarketActivity(string id,bool accept,string reviewer,Guid? commandId=null)=>marketActivityStore.ReviewMarketActivity(id,accept,reviewer,commandId);
    public DateOnly? ReadMarketActivityCursor()=>marketActivityStore.ReadMarketActivityCursor();
    public void RecordMarketActivityCursor(DateOnly day)=>marketActivityStore.RecordMarketActivityCursor(day);
    public void SaveMarketActivity(string market,DateOnly day,MarketActivitySource? source)=>marketActivityStore.SaveMarketActivity(market,day,source);
    public List<MarketActivityPoint> MarketActivity()=>marketActivityStore.MarketActivity();
}
