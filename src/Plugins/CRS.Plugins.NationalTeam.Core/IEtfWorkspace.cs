using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>页面只接触用例契约；本地后台实现和管道客户端可分别验证。</summary>
public interface IEtfWorkspace
{
    string NetworkStatus { get; }
    bool CanManage=>true;
    Task<List<EtfSummary>> OverviewAsync(CancellationToken token);
    Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token);
    Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token);
    Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token);
    Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token);
    Task ExportEvidenceAsync(string revisionId,bool original,string destination,CancellationToken token);
    Task ReviewAsync(string id,bool accept,string reviewer,bool comparableConfirmed,CancellationToken token);
    Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token);
}

/// <summary>受限业务参数不携带本机路径、类型名或任意 SQL；写操作随 RunNow 持久化。</summary>
public sealed record EtfRequest(string Operation,string? Market=null,string? Ticker=null,DateOnly? From=null,
    DateOnly? To=null,string? Revision=null,bool Accept=false,string? Reviewer=null,bool Comparable=false,
    string? Name=null,string? Index=null,bool Active=false,string? Reason=null,int Offset=0,int Count=10,bool Original=false,string? Snapshot=null);
/// <summary>分页使用服务端短期快照，避免采集同时写入导致桌面分页重复或遗漏。</summary>
public sealed record EtfQueryPage(string Items,string Snapshot,int? NextOffset,string? Metadata=null);
/// <summary>证据导出按有界字节块返回，桌面自行选择输出位置，服务不接收用户路径。</summary>
public sealed record EvidenceChunk(string Hash,long Length,int Offset,byte[] Bytes);
