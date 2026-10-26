using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>Compatibility bridge for old composite workspaces while market-cycle consumers move to the narrow port.</summary>
internal sealed class LegacyMarketCycleWorkspaceAdapter(IEtfWorkspace inner):IMarketCycleWorkspace
{
    public bool CanManage=>inner.CanManage;
    public Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token)=>inner.MarketActivityAsync(token);
    public Task SyncMarketActivityAsync(DateOnly from,DateOnly to,CancellationToken token)=>inner.SyncMarketActivityAsync(from,to,token);
    public Task<List<MarketActivityRevision>> MarketActivityRevisionsAsync(CancellationToken token)=>inner.MarketActivityRevisionsAsync(token);
    public Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token)=>inner.ReviewMarketActivityAsync(id,accept,reviewer,token);
    public Task<IndexSeriesResearchReport> IndexSeriesReportAsync(DateOnly from,DateOnly through,CancellationToken token)=>inner.IndexSeriesReportAsync(from,through,token);
    public Task<List<IndexSeriesRevision>> IndexSeriesRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token)=>inner.IndexSeriesRevisionsAsync(from,through,token);
    public Task<IndexSeriesCommandReceipt> QueueIndexSeriesAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token)=>inner.QueueIndexSeriesAsync(from,through,refreshSource,token);
    public Task<IndexSeriesCommandReceipt> QueueIndexValuationAsync(DateOnly date,CancellationToken token)=>inner.QueueIndexValuationAsync(date,token);
    public Task<IndexSeriesCommandReceipt> IndexSeriesStatusAsync(Guid requestId,CancellationToken token)=>inner.IndexSeriesStatusAsync(requestId,token);
    public static IMarketCycleWorkspace Create(IEtfWorkspace workspace)
        =>workspace as IMarketCycleWorkspace??new LegacyMarketCycleWorkspaceAdapter(workspace);
}
