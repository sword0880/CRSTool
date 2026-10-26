using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for official industry PE observations, revisions, and collection receipts.</summary>
public interface IIndustryValuationWorkspace : IWorkspaceAccess
{
    Task<IndustryPeReport> IndustryPeReportAsync(DateOnly from,DateOnly through,CancellationToken token);
    Task<List<IndustryPeRevision>> IndustryPeRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token);
    Task<IndustryPeSyncReceipt> QueueIndustryPeAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token);
    Task<IndustryPeSyncReceipt> IndustryPeSyncStatusAsync(Guid requestId,CancellationToken token);
}
