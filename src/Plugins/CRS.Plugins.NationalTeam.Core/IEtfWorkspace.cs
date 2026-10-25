using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>页面只接触用例契约；本地后台实现和管道客户端可分别验证。</summary>
public interface IEtfWorkspace
{
    Task<ExitAlertOverview> AlertOverviewAsync(CancellationToken token)=>throw new NotSupportedException("预警中心需要更新后台服务。");
    Task<ExitAlertConfiguration> AlertConfigurationAsync(CancellationToken token)=>throw new NotSupportedException();
    Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token)=>throw new NotSupportedException();
    Task<List<ExitAlertEvent>> AlertEventsAsync(ExitAlertFilter filter,CancellationToken token)=>throw new NotSupportedException();
    Task<ExitAlertEvent> AlertEventAsync(string id,CancellationToken token)=>throw new NotSupportedException();
    Task<ExitAlertEvent> AlertEventVersionAsync(string id,int sequence,CancellationToken token)=>throw new NotSupportedException();
    Task<List<EvidenceRow>> AlertInputsAsync(string id,int? sequence,CancellationToken token)=>throw new NotSupportedException();
    Task<List<ExitAlertChange>> AlertChangesAsync(string id,CancellationToken token)=>throw new NotSupportedException();
    Task<List<AlertOutboxItem>> AlertOutboxAsync(CancellationToken token)=>throw new NotSupportedException();
    Task<List<AlertOutboxItem>> AlertOutboxAsync(ExitAlertFilter filter,CancellationToken token)=>AlertOutboxAsync(token);
    Task<List<AlertDeliveryRecord>> AlertDeliveriesAsync(string id,CancellationToken token)=>throw new NotSupportedException();
    Task RetryAlertNotificationAsync(string id,CancellationToken token)=>throw new NotSupportedException();
    Task EvaluateAlertsAsync(CancellationToken token)=>throw new NotSupportedException();
    Task<List<AlertChannel>> AlertChannelsAsync(CancellationToken token)=>throw new NotSupportedException();
    Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token)=>throw new NotSupportedException();
    Task RegisterAlertDocumentAsync(AlertDocumentRequest request,CancellationToken token)=>throw new NotSupportedException();
    Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token)=>throw new NotSupportedException();
    Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token)=>throw new NotSupportedException();
    Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token)=>throw new NotSupportedException();
    Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token)=>throw new NotSupportedException();
    string NetworkStatus { get; }
    bool CanManage=>true;
    Task<List<EtfSummary>> OverviewAsync(CancellationToken token);
    Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token);
    Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token);
    Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token);
    /// <summary>单只证券证据由服务端过滤后分页，避免历史扩展后全池证据超过传输上限。</summary>
    async Task<List<EvidenceRow>> EvidenceAsync(EtfKey key,CancellationToken token)=>(await EvidenceAsync(token)).Where(x=>x.Market==key.Market&&x.Ticker==key.Ticker).ToList();
    Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token)=>throw new NotSupportedException("市场温度需要更新后台服务。");
    Task SyncMarketActivityAsync(DateOnly from,DateOnly to,CancellationToken token)=>throw new NotSupportedException();
    Task<List<MarketActivityRevision>> MarketActivityRevisionsAsync(CancellationToken token)=>throw new NotSupportedException();
    Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token)=>throw new NotSupportedException();
    Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token)=>throw new NotSupportedException("历史汇总需要更新后台服务。");
    Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token);
    /// <summary>NAV保持独立修订身份；旧后台显式拒绝，不回退为份额点上的未知金额。</summary>
    Task<List<NavAcceptedPoint>> NavPointsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token)=>throw new NotSupportedException("NAV查询需要更新后台服务。");
    Task<List<NavRevision>> NavRevisionsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token)=>throw new NotSupportedException("NAV修订需要更新后台服务。");
    /// <summary>行业公布值独立读取；旧服务明确拒绝，不把未支持或超时转换为空图。</summary>
    Task<IndustryPeReport> IndustryPeReportAsync(DateOnly from,DateOnly through,CancellationToken token)=>throw new NotSupportedException("行业研究需要更新后台服务。");
    Task<List<IndustryPeRevision>> IndustryPeRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token)=>throw new NotSupportedException("行业修订需要更新后台服务。");
    Task<IndustryPeSyncReceipt> QueueIndustryPeAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token)=>throw new NotSupportedException("行业采集需要更新后台服务。");
    Task<IndustryPeSyncReceipt> IndustryPeSyncStatusAsync(Guid requestId,CancellationToken token)=>throw new NotSupportedException("行业任务需要更新后台服务。");
    Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token);
    Task<List<CollectionAttempt>> CollectionAttemptsAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>Task.FromResult(new List<CollectionAttempt>());
    Task<List<EtfAcceptance>> AcceptanceAsync(DateOnly through,CancellationToken token)=>throw new NotSupportedException("采集验收需要更新后台服务。");
    Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token);
    Task ExportEvidenceAsync(string revisionId,bool original,string destination,CancellationToken token);
    Task ReviewAsync(string id,bool accept,string reviewer,bool comparableConfirmed,CancellationToken token);
    Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token);
    Task<List<EquityWatch>> EquityWatchlistAsync(CancellationToken token)=>throw new NotSupportedException("直接持股需要更新后台服务。");
    Task<List<EquityHoldingRow>> EquityHoldingsAsync(string? search,string? institution,string? nature,bool favorites,CancellationToken token)=>throw new NotSupportedException("直接持股需要更新后台服务。");
    Task<List<EquityDisclosure>> EquityDisclosuresAsync(EtfKey key,CancellationToken token)=>throw new NotSupportedException();
    Task<List<EquityHoldingRow>> EquityEventsAsync(EtfKey key,CancellationToken token)=>throw new NotSupportedException("历次变动需要更新后台服务。");
    Task<List<EquityReportDocument>> EquityReportsAsync(EtfKey key,CancellationToken token)=>throw new NotSupportedException("官方报告目录需要更新后台服务。");
    Task<EquityReportSearch> QueueEquityReportsAsync(EtfKey key,CancellationToken token)=>throw new NotSupportedException();
    Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token)=>throw new NotSupportedException();
    Task SyncEquityAsync(CancellationToken token)=>throw new NotSupportedException();
    Task<IndexSeriesResearchReport> IndexSeriesReportAsync(DateOnly from,DateOnly through,CancellationToken token)=>throw new NotSupportedException("指数研究需要更新后台服务。");
    Task<List<IndexSeriesRevision>> IndexSeriesRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token)=>throw new NotSupportedException("指数修订查询需要更新后台服务。");
    Task<IndexSeriesCommandReceipt> QueueIndexSeriesAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token)=>throw new NotSupportedException("指数采集需要更新后台服务。");
    Task<IndexSeriesCommandReceipt> QueueIndexValuationAsync(DateOnly date,CancellationToken token)=>throw new NotSupportedException("指数估值采集需要更新后台服务。");
    Task<IndexSeriesCommandReceipt> IndexSeriesStatusAsync(Guid requestId,CancellationToken token)=>throw new NotSupportedException("指数任务状态需要更新后台服务。");
    Task ReviewEquityAsync(EquityReview review,CancellationToken token)=>throw new NotSupportedException();
}

/// <summary>受限业务参数不携带本机路径、类型名或任意 SQL；写操作随 RunNow 持久化。</summary>
public sealed record EtfRequest(string Operation,string? Market=null,string? Ticker=null,DateOnly? From=null,
    DateOnly? To=null,string? Revision=null,bool Accept=false,string? Reviewer=null,bool Comparable=false,
    string? Name=null,string? Index=null,bool Active=false,string? Reason=null,int Offset=0,int Count=10,bool Original=false,string? Snapshot=null,long? ConfigurationRevision=null,
    string? Search=null,string? Institution=null,string? Nature=null,bool Favorites=false,EquityWatch? EquityWatch=null,EquityReview? EquityReview=null,
    ExitAlertFilter? AlertFilter=null,ExitAlertConfiguration? AlertConfiguration=null,AlertDocumentRequest? AlertDocument=null,
    AlertMetadataReview? AlertMetadata=null,NamedEtfHoldingReview? NamedEtfHolding=null,int ContractVersion=EtfBusinessProtocol.Version,bool RefreshSource=false);
/// <summary>分页使用服务端短期快照，避免采集同时写入导致桌面分页重复或遗漏。</summary>
public sealed record EtfQueryPage(string Items,string Snapshot,int? NextOffset,string? Metadata=null,int ContractVersion=EtfBusinessProtocol.Version)
{
    // V1可选能力，不改原构造签名；缺失表示旧服务仍按十行读取，页字节限额不随行数增加。
    public int? MaximumPageCount {get;init;}
    // 同一快照的完整行数；新端据此识别提前结束，旧服务缺失时沿既有游标契约读取。
    public int? TotalRows {get;init;}
}
/// <summary>证据导出按有界字节块返回，桌面自行选择输出位置，服务不接收用户路径。</summary>
public sealed record EvidenceChunk(string Hash,long Length,int Offset,byte[] Bytes,int ContractVersion=EtfBusinessProtocol.Version);
