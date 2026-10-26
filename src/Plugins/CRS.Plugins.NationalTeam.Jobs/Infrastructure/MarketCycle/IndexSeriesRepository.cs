using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>Compatibility facade for index-series facts; persistence lives in IndexSeriesStore.</summary>
public sealed partial class MarketRepository
{
    public IndexCalendarSnapshot IndexSeriesCalendar(DateOnly from,DateOnly through,DateTimeOffset cutoff)
    {
        var snapshot=marketCalendarStore.ReadPublishedTradingCalendar(from,through,cutoff);
        return new(snapshot.Days,snapshot.Fingerprint);
    }

    private void InitializeIndexSeriesSchema()=>indexSeriesStore.InitializeSchema();
    public IndexImportOutcome? IndexSeriesReceipt(Guid commandId)=>indexSeriesStore.IndexSeriesReceipt(commandId);
    public IndexImportOutcome ImportIndexSeries(CapturedIndexPacket packet,Guid? commandId=null)=>indexSeriesStore.ImportIndexSeries(packet,commandId);
    public IReadOnlyList<IndexSeriesRevision> IndexSeriesRevisions(DateOnly from,DateOnly through,DateTimeOffset cutoff)=>indexSeriesStore.IndexSeriesRevisions(from,through,cutoff);
    public void ReviewIndexSeries(string revision,bool accept,string reviewer,Guid? commandId=null)=>indexSeriesStore.ReviewIndexSeries(revision,accept,reviewer,commandId);
}
