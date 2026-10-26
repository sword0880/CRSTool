using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>
/// Transitional adapter for legacy IEtfWorkspace implementations. New application paths should inject IEtfResearchWorkspace directly.
/// </summary>
internal sealed class LegacyEtfResearchWorkspaceAdapter(IEtfWorkspace inner):IEtfResearchWorkspace
{
    public string NetworkStatus=>inner.NetworkStatus;
    public bool CanManage=>inner.CanManage;
    public Task<List<EtfSummary>> OverviewAsync(CancellationToken token)=>inner.OverviewAsync(token);
    public Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>inner.ConfigurationAsync(token);
    public Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token)=>inner.SaveConfigurationAsync(key,name,index,active,reason,expectedRevision,token);
    public Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token)=>inner.EvidenceAsync(token);
    public Task<List<EvidenceRow>> EvidenceAsync(EtfKey key,CancellationToken token)=>inner.EvidenceAsync(key,token);
    public Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token)=>inner.HistoryAsync(key,token);
    public Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token)=>inner.PointsAsync(key,token);
    public Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>inner.CoverageAsync(key,from,to,token);
    public Task<List<CollectionAttempt>> CollectionAttemptsAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>inner.CollectionAttemptsAsync(key,from,to,token);
    public Task<List<EtfAcceptance>> AcceptanceAsync(DateOnly through,CancellationToken token)=>inner.AcceptanceAsync(through,token);
    public Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token)=>inner.SyncAsync(from,to,token);
    public Task ExportEvidenceAsync(string revisionId,bool original,string destination,CancellationToken token)=>inner.ExportEvidenceAsync(revisionId,original,destination,token);
    public Task ReviewAsync(string id,bool accept,string reviewer,bool comparableConfirmed,CancellationToken token)=>inner.ReviewAsync(id,accept,reviewer,comparableConfirmed,token);
    public Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token)=>inner.MembershipAsync(key,name,index,active,reason,token);
    public static IEtfResearchWorkspace Create(IEtfWorkspace workspace)
        =>workspace as IEtfResearchWorkspace??new LegacyEtfResearchWorkspaceAdapter(workspace);
}
