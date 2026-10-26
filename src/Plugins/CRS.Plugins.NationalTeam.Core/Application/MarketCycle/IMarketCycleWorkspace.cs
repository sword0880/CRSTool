using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for market activity and reviewed index-series research.</summary>
public interface IMarketCycleWorkspace : IWorkspaceAccess
{
    Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token) => throw new NotSupportedException("市场温度需要更新后台服务。");
    Task SyncMarketActivityAsync(DateOnly from,DateOnly to,CancellationToken token) => throw new NotSupportedException("市场温度采集需要更新后台服务。");
    Task<List<MarketActivityRevision>> MarketActivityRevisionsAsync(CancellationToken token) => throw new NotSupportedException("市场温度修订需要更新后台服务。");
    Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token) => throw new NotSupportedException("市场温度审核需要更新后台服务。");
    Task<IndexSeriesResearchReport> IndexSeriesReportAsync(DateOnly from,DateOnly through,CancellationToken token) => throw new NotSupportedException("指数研究需要更新后台服务。");
    Task<List<IndexSeriesRevision>> IndexSeriesRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token) => throw new NotSupportedException("指数修订查询需要更新后台服务。");
    Task<IndexSeriesCommandReceipt> QueueIndexSeriesAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token) => throw new NotSupportedException("指数采集需要更新后台服务。");
    Task<IndexSeriesCommandReceipt> QueueIndexValuationAsync(DateOnly date,CancellationToken token) => throw new NotSupportedException("指数估值采集需要更新后台服务。");
    Task<IndexSeriesCommandReceipt> IndexSeriesStatusAsync(Guid requestId,CancellationToken token) => throw new NotSupportedException("指数任务状态需要更新后台服务。");
}
