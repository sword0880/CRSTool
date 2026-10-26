using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for institutional direct-holding disclosures and historical-source registration.</summary>
public interface IDirectEquityWorkspace : IWorkspaceAccess
{
    Task<List<HistoricalEquityPdf>> HistoricalEquityDocumentsAsync(CancellationToken token);
    Task<HistoricalEquityCommand> ImportHistoricalEquityPdfAsync(string id,string reviewer,string reason,bool originalConfirmed,CancellationToken token);
    Task<HistoricalEquityCommand> HistoricalEquityStatusAsync(Guid requestId,CancellationToken token);
    Task<HistoricalEquityReceipt?> HistoricalEquityReceiptAsync(Guid requestId,CancellationToken token);
    Task<List<EquityWatch>> EquityWatchlistAsync(CancellationToken token);
    Task<List<EquityHoldingRow>> EquityHoldingsAsync(string? search,string? institution,string? nature,bool favorites,CancellationToken token);
    Task<List<EquityDisclosure>> EquityDisclosuresAsync(EtfKey key,CancellationToken token);
    Task<List<EquityHoldingRow>> EquityEventsAsync(EtfKey key,CancellationToken token);
    Task<List<EquityReportDocument>> EquityReportsAsync(EtfKey key,CancellationToken token);
    Task<EquityReportSearch> QueueEquityReportsAsync(EtfKey key,CancellationToken token);
    Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token);
    Task SyncEquityAsync(CancellationToken token);
    Task ReviewEquityAsync(EquityReview review,CancellationToken token);
}
