using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>
/// ETF界面研究与证据命令端口；所有能力必须由客户端显式实现。
/// </summary>
public interface IEtfResearchWorkspace : IWorkspaceAccess
{
    string NetworkStatus { get; }
    Task<List<EtfSummary>> OverviewAsync(CancellationToken token);
    Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token);
    Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token);
    Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token);
    Task<List<EvidenceRow>> EvidenceAsync(EtfKey key,CancellationToken token);
    Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token);
    Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token);
    Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token);
    Task<List<CollectionAttempt>> CollectionAttemptsAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token);
    Task<List<EtfAcceptance>> AcceptanceAsync(DateOnly through,CancellationToken token);
    Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token);
    Task ExportEvidenceAsync(string revisionId,bool original,string destination,CancellationToken token);
    Task ReviewAsync(string id,bool accept,string reviewer,bool comparableConfirmed,CancellationToken token);
    Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token);
}
