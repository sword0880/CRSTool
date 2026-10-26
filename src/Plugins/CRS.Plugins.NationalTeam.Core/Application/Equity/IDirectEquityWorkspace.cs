using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for institutional direct-holding disclosures and historical-source registration.</summary>
public interface IDirectEquityWorkspace : IWorkspaceAccess
{
    Task<List<HistoricalEquityPdf>> HistoricalEquityDocumentsAsync(CancellationToken token) => throw new NotSupportedException("历史原文登记需要更新后台。");
    Task<HistoricalEquityCommand> ImportHistoricalEquityPdfAsync(string id,string reviewer,string reason,bool originalConfirmed,CancellationToken token) => throw new NotSupportedException("历史原文登记需要更新后台。");
    Task<HistoricalEquityCommand> HistoricalEquityStatusAsync(Guid requestId,CancellationToken token) => throw new NotSupportedException("历史原文登记需要更新后台。");
    Task<HistoricalEquityReceipt?> HistoricalEquityReceiptAsync(Guid requestId,CancellationToken token) => throw new NotSupportedException("历史原文回执需要更新后台。");
    Task<List<EquityWatch>> EquityWatchlistAsync(CancellationToken token) => throw new NotSupportedException("直接持股需要更新后台服务。");
    Task<List<EquityHoldingRow>> EquityHoldingsAsync(string? search,string? institution,string? nature,bool favorites,CancellationToken token) => throw new NotSupportedException("直接持股需要更新后台服务。");
    Task<List<EquityDisclosure>> EquityDisclosuresAsync(EtfKey key,CancellationToken token) => throw new NotSupportedException("持股披露需要更新后台服务。");
    Task<List<EquityHoldingRow>> EquityEventsAsync(EtfKey key,CancellationToken token) => throw new NotSupportedException("历次变动需要更新后台服务。");
    Task<List<EquityReportDocument>> EquityReportsAsync(EtfKey key,CancellationToken token) => throw new NotSupportedException("官方报告目录需要更新后台服务。");
    Task<EquityReportSearch> QueueEquityReportsAsync(EtfKey key,CancellationToken token) => throw new NotSupportedException("官方报告检索需要更新后台服务。");
    Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token) => throw new NotSupportedException("直接持股观察配置需要更新后台服务。");
    Task SyncEquityAsync(CancellationToken token) => throw new NotSupportedException("直接持股同步需要更新后台服务。");
    Task ReviewEquityAsync(EquityReview review,CancellationToken token) => throw new NotSupportedException("持股披露审核需要更新后台服务。");
}
