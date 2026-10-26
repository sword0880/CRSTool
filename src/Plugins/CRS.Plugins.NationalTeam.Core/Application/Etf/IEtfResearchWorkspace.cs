using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>
/// ETF份额研究与证据操作的独立端口；实现缺失时显式报告不可用。
/// </summary>
public interface IEtfResearchWorkspace : IWorkspaceAccess
{
    string NetworkStatus { get; }
    Task<List<EtfSummary>> OverviewAsync(CancellationToken token) => throw new NotSupportedException("ETF概览需要更新后台服务。");
    Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token) => throw new NotSupportedException("ETF配置需要更新后台服务。");
    Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token) => throw new NotSupportedException("ETF配置写入需要更新后台服务。");
    Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token) => throw new NotSupportedException("ETF证据需要更新后台服务。");
    async Task<List<EvidenceRow>> EvidenceAsync(EtfKey key,CancellationToken token) => (await EvidenceAsync(token)).Where(x=>x.Market==key.Market&&x.Ticker==key.Ticker).ToList();
    Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token) => throw new NotSupportedException("ETF历史需要更新后台服务。");
    Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token) => throw new NotSupportedException("ETF份额点需要更新后台服务。");
    Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token) => throw new NotSupportedException("ETF覆盖率需要更新后台服务。");
    Task<List<CollectionAttempt>> CollectionAttemptsAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token) => throw new NotSupportedException("该工作区未提供采集记录查询。");
    Task<List<EtfAcceptance>> AcceptanceAsync(DateOnly through,CancellationToken token) => throw new NotSupportedException("采集验收需要更新后台服务。");
    Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token) => throw new NotSupportedException("ETF同步需要更新后台服务。");
    Task ExportEvidenceAsync(string revisionId,bool original,string destination,CancellationToken token) => throw new NotSupportedException("证据导出需要更新后台服务。");
    Task ReviewAsync(string id,bool accept,string reviewer,bool comparableConfirmed,CancellationToken token) => throw new NotSupportedException("份额审核需要更新后台服务。");
    Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token) => throw new NotSupportedException("ETF观察池管理需要更新后台服务。");
}
