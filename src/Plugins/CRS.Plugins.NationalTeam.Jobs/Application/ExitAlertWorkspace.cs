using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>预警仍共用既有市场库门槛；联网投递由宿主适配，不能在数据库锁内等待提供商。</summary>
public sealed partial class EtfWorkspace
{
    // 写入用例只依赖本模块端口，避免扩大对其他业务仓储的依赖。
    private IExitAlertWriteRepository AlertWrites=>repository!;

    public Task<ExitAlertOverview> AlertOverviewAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertOverview(),token);
    public Task<ExitAlertConfiguration> AlertConfigurationAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertConfiguration(),token);
    public Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token)=>SaveAlertConfigurationAsync(value,token,null);
    public Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token,Guid? commandId)=>RunAsync(()=>{AlertWrites.SaveAlertConfiguration(value,commandId);return true;},token);
    public Task<List<ExitAlertEvent>> AlertEventsAsync(ExitAlertFilter filter,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertEvents(filter),token);
    public Task<ExitAlertEvent> AlertEventAsync(string id,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertEvent(id)??throw new InvalidDataException("事件不存在。"),token);
    public Task<ExitAlertEvent> AlertEventVersionAsync(string id,int sequence,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertEvent(id,sequence)??throw new InvalidDataException("历史事件版本不存在。"),token);
    public Task<List<EvidenceRow>> AlertInputsAsync(string id,int? sequence,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertInputs(id,sequence),token);
    public Task<List<ExitAlertChange>> AlertChangesAsync(string id,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertChanges(id),token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertOutbox(),token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(ExitAlertFilter filter,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertOutbox(filter),token);
    public Task<List<AlertDeliveryRecord>> AlertDeliveriesAsync(string id,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertDeliveries(id),token);
    public Task RetryAlertNotificationAsync(string id,CancellationToken token)=>RetryAlertNotificationAsync(id,token,null);
    public Task RetryAlertNotificationAsync(string id,CancellationToken token,Guid? commandId)=>RunAsync(()=>{AlertWrites.RetryAlertNotification(id,commandId);return true;},token);
    public Task EvaluateAlertsAsync(CancellationToken token)=>EvaluateAlertsAsync(false,token);
    public Task EvaluateAlertsAsync(bool force,CancellationToken token)=>RunAsync(()=>AlertWrites.EvaluateExitAlerts(force),token);
    public Task<AlertOutboxItem?> ClaimAlertNotificationAsync(CancellationToken token)=>RunAsync(()=>AlertWrites.ClaimAlertNotification(),token);
    public Task CompleteAlertNotificationAsync(AlertOutboxItem item,AlertDispatchOutcome result)=>RunAsync(()=>{AlertWrites.CompleteAlertNotification(item.Id,item.Lease!,result);return true;},CancellationToken.None);
    public Task<List<AlertChannel>> AlertChannelsAsync(CancellationToken token)=>Task.FromResult(new List<AlertChannel>());
    public Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertDocuments(key),token);
    public Task RegisterAlertDocumentAsync(AlertDocumentRequest request,CancellationToken token)=>throw new NotSupportedException("原件由Worker按官方URL获取。");
    public Task RegisterAlertDocumentAsync(AlertDocumentRequest request,byte[] bytes,CancellationToken token,Guid? commandId)=>RunAsync(()=>{AlertWrites.RegisterAlertDocument(request,bytes,commandId);return true;},token);
    public Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertMetadata(),token);
    public Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token)=>ReviewAlertMetadataAsync(review,token,null);
    public Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token,Guid? commandId)=>RunAsync(()=>{AlertWrites.ReviewAlertMetadata(review,commandId);return true;},token);
    public Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.NamedEtfHoldings(key),token);
    public Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token)=>ReviewNamedEtfHoldingAsync(review,token,null);
    public Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token,Guid? commandId)=>RunAsync(()=>{AlertWrites.ReviewNamedEtfHolding(review,commandId);return true;},token);
}
