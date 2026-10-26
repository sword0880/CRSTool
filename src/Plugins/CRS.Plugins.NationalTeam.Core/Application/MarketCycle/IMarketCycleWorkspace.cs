using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for market activity and reviewed index-series research.</summary>
public interface IMarketCycleWorkspace : IWorkspaceAccess
{
    Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token);
    Task SyncMarketActivityAsync(DateOnly from,DateOnly to,CancellationToken token);
    Task<List<MarketActivityRevision>> MarketActivityRevisionsAsync(CancellationToken token);
    Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token);
    Task<IndexSeriesResearchReport> IndexSeriesReportAsync(DateOnly from,DateOnly through,CancellationToken token);
    Task<List<IndexSeriesRevision>> IndexSeriesRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token);
    Task<IndexSeriesCommandReceipt> QueueIndexSeriesAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token);
    Task<IndexSeriesCommandReceipt> QueueIndexValuationAsync(DateOnly date,CancellationToken token);
    Task<IndexSeriesCommandReceipt> IndexSeriesStatusAsync(Guid requestId,CancellationToken token);
}
