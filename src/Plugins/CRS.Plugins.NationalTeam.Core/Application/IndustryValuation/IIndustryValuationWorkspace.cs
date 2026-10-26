using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for official industry PE observations, revisions, and collection receipts.</summary>
public interface IIndustryValuationWorkspace : IWorkspaceAccess
{
    Task<IndustryPeReport> IndustryPeReportAsync(DateOnly from,DateOnly through,CancellationToken token) => throw new NotSupportedException("行业研究需要更新后台服务。");
    Task<List<IndustryPeRevision>> IndustryPeRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token) => throw new NotSupportedException("行业修订需要更新后台服务。");
    Task<IndustryPeSyncReceipt> QueueIndustryPeAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token) => throw new NotSupportedException("行业采集需要更新后台服务。");
    Task<IndustryPeSyncReceipt> IndustryPeSyncStatusAsync(Guid requestId,CancellationToken token) => throw new NotSupportedException("行业任务需要更新后台服务。");
}
