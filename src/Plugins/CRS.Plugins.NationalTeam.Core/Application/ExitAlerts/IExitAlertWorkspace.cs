using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for exit-alert evaluation, review, evidence, and delivery workflows.</summary>
public interface IExitAlertWorkspace : IWorkspaceAccess
{
    Task<ExitAlertOverview> AlertOverviewAsync(CancellationToken token) => throw new NotSupportedException("预警中心需要更新后台服务。");
    Task<ExitAlertConfiguration> AlertConfigurationAsync(CancellationToken token) => throw new NotSupportedException("预警配置需要更新后台服务。");
    Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token) => throw new NotSupportedException("预警配置写入需要更新后台服务。");
    Task<List<ExitAlertEvent>> AlertEventsAsync(ExitAlertFilter filter,CancellationToken token) => throw new NotSupportedException("预警事件需要更新后台服务。");
    Task<ExitAlertEvent> AlertEventAsync(string id,CancellationToken token) => throw new NotSupportedException("预警事件需要更新后台服务。");
    Task<ExitAlertEvent> AlertEventVersionAsync(string id,int sequence,CancellationToken token) => throw new NotSupportedException("预警历史版本需要更新后台服务。");
    Task<List<EvidenceRow>> AlertInputsAsync(string id,int? sequence,CancellationToken token) => throw new NotSupportedException("预警输入证据需要更新后台服务。");
    Task<List<ExitAlertChange>> AlertChangesAsync(string id,CancellationToken token) => throw new NotSupportedException("预警变化需要更新后台服务。");
    Task<List<AlertOutboxItem>> AlertOutboxAsync(CancellationToken token) => throw new NotSupportedException("预警发件箱需要更新后台服务。");
    Task<List<AlertOutboxItem>> AlertOutboxAsync(ExitAlertFilter filter,CancellationToken token) => AlertOutboxAsync(token);
    Task<List<AlertDeliveryRecord>> AlertDeliveriesAsync(string id,CancellationToken token) => throw new NotSupportedException("预警投递记录需要更新后台服务。");
    Task RetryAlertNotificationAsync(string id,CancellationToken token) => throw new NotSupportedException("预警通知重试需要更新后台服务。");
    Task EvaluateAlertsAsync(CancellationToken token) => throw new NotSupportedException("预警评估需要更新后台服务。");
    Task<List<AlertChannel>> AlertChannelsAsync(CancellationToken token) => throw new NotSupportedException("预警渠道需要更新后台服务。");
    Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token) => throw new NotSupportedException("预警文档需要更新后台服务。");
    Task RegisterAlertDocumentAsync(AlertDocumentRequest request,CancellationToken token) => throw new NotSupportedException("预警文档登记需要更新后台服务。");
    Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token) => throw new NotSupportedException("基金元数据需要更新后台服务。");
    Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token) => throw new NotSupportedException("基金元数据审核需要更新后台服务。");
    Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token) => throw new NotSupportedException("ETF具名持仓需要更新后台服务。");
    Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token) => throw new NotSupportedException("ETF具名持仓审核需要更新后台服务。");
}
