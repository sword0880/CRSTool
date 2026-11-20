// 市场周期、广度和成交知识研究的远程窄接口；来源资料、研究快照与复核操作通过后台完成。
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for market activity and reviewed index-series research.</summary>
public interface IMarketCycleWorkspace : IWorkspaceAccess
{
    Task<MarketBreadthHistory> MarketBreadthHistoryAsync(DateOnly date,CancellationToken token);
    Task<MarketBreadthSubmissionReceipt> SubmitMarketBreadthAsync(string originalPath,string normalizedPath,string sourceUrl,DateTimeOffset retrievedAt,string submitter,string reason,CancellationToken token,IProgress<(int Received,int Total)>? progress=null);
    Task ReviewMarketBreadthAsync(MarketBreadthReview review,CancellationToken token);
    Task<MarketBreadthSubmissionReceipt> ConfirmMarketBreadthSubmissionAsync(CancellationToken token);
    Task ExportMarketBreadthEvidenceAsync(string revision,bool original,string destination,CancellationToken token);
    Task<MarketCycleStudyReport> MarketCycleStudyAsync(DateOnly asOf,DateTimeOffset? cutoff,CancellationToken token);
    Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token);
    Task SyncMarketActivityAsync(DateOnly from,DateOnly to,CancellationToken token);
    Task<List<MarketActivityRevision>> MarketActivityRevisionsAsync(CancellationToken token);
    Task<MarketActivityKnowledgeReport> MarketActivityKnowledgeAsync(DateOnly from,DateOnly through,DateTimeOffset? cutoff,CancellationToken token);
    Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token);
    Task<IndexSeriesResearchReport> IndexSeriesReportAsync(DateOnly from,DateOnly through,CancellationToken token);
    Task<List<IndexSeriesRevision>> IndexSeriesRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token);
    Task<IndexSeriesCommandReceipt> QueueIndexSeriesAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token);
    Task<IndexSeriesCommandReceipt> QueueIndexValuationAsync(DateOnly date,CancellationToken token);
    Task<IndexSeriesCommandReceipt> IndexSeriesStatusAsync(Guid requestId,CancellationToken token);
}
