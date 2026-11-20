using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>ETF持仓研究的远程目录合同；所有来源采集、持久化及解析在后台执行。</summary>
public interface IEtfHoldingsWorkspace:IWorkspaceAccess
{
    Task<NamedEtfHoldingHistoryReport> EtfHoldingHistoryAsync(EtfKey key,DateOnly asOf,CancellationToken token);
    Task<EtfReportIndexSnapshot> EtfReportIndexAsync(EtfKey key,CancellationToken token);
    Task<EtfReportSearchReceipt> QueueEtfReportsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token);
    Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token);
    Task RegisterAlertDocumentAsync(AlertDocumentRequest request,CancellationToken token);
    Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token);
    Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token);
    Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token);
    Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token);
    Task<EtfHolderCandidateReport> EtfHolderCandidatesAsync(EtfKey key,string documentId,CancellationToken token);
}
