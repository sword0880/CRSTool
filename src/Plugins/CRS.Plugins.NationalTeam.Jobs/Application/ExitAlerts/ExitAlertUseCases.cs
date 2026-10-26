using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>预警仍共用既有市场库门槛；联网投递由宿主适配，不能在数据库锁内等待提供商。</summary>
public sealed partial class ExitAlertUseCases : MarketUseCaseScope
{
    internal ExitAlertUseCases(MarketApplicationSession session):base(session){}

    public Task<ExitAlertOverview> AlertOverviewAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertOverview(),token);
    public Task<ExitAlertConfiguration> AlertConfigurationAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertConfiguration(),token);
    public Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token)=>SaveAlertConfigurationAsync(value,token,null);
    public Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token,Guid? commandId)=>RunAsync((IExitAlertWriteRepository repository)=>{repository.SaveAlertConfiguration(value,commandId);return true;},token);
    public Task<List<ExitAlertEvent>> AlertEventsAsync(ExitAlertFilter filter,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertEvents(filter),token);
    public Task<ExitAlertEvent> AlertEventAsync(string id,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertEvent(id)??throw new InvalidDataException("事件不存在。"),token);
    public Task<ExitAlertEvent> AlertEventVersionAsync(string id,int sequence,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertEvent(id,sequence)??throw new InvalidDataException("历史事件版本不存在。"),token);
    public Task<List<EvidenceRow>> AlertInputsAsync(string id,int? sequence,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertInputs(id,sequence),token);
    public Task<List<ExitAlertChange>> AlertChangesAsync(string id,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertChanges(id),token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertOutbox(),token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(ExitAlertFilter filter,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertOutbox(filter),token);
    public Task<List<AlertDeliveryRecord>> AlertDeliveriesAsync(string id,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertDeliveries(id),token);
    public Task RetryAlertNotificationAsync(string id,CancellationToken token)=>RetryAlertNotificationAsync(id,token,null);
    public Task RetryAlertNotificationAsync(string id,CancellationToken token,Guid? commandId)=>RunAsync((IExitAlertWriteRepository repository)=>{repository.RetryAlertNotification(id,commandId);return true;},token);
    public Task EvaluateAlertsAsync(CancellationToken token)=>EvaluateAlertsAsync(false,token);
    public Task EvaluateAlertsAsync(bool force,CancellationToken token)=>RunAsync((IExitAlertWriteRepository repository)=>repository.EvaluateExitAlerts(force),token);
    public Task<AlertOutboxItem?> ClaimAlertNotificationAsync(CancellationToken token)=>RunAsync((IExitAlertWriteRepository repository)=>repository.ClaimAlertNotification(),token);
    public Task CompleteAlertNotificationAsync(AlertOutboxItem item,AlertDispatchOutcome result)=>RunAsync((IExitAlertWriteRepository repository)=>{repository.CompleteAlertNotification(item.Id,item.Lease!,result);return true;},CancellationToken.None);
    public Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertDocuments(key),token);
    public Task RegisterAlertDocumentAsync(AlertDocumentRequest request,byte[] bytes,CancellationToken token,Guid? commandId)=>RunAsync((IExitAlertWriteRepository repository)=>{repository.RegisterAlertDocument(request,bytes,commandId);return true;},token);
    public Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.AlertMetadata(),token);
    public Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token)=>ReviewAlertMetadataAsync(review,token,null);
    public Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token,Guid? commandId)=>RunAsync((IExitAlertWriteRepository repository)=>{repository.ReviewAlertMetadata(review,commandId);return true;},token);
    public Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IExitAlertReadRepository current)=>current.NamedEtfHoldings(key),token);
    public Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token)=>ReviewNamedEtfHoldingAsync(review,token,null);
    public Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token,Guid? commandId)=>RunAsync((IExitAlertWriteRepository repository)=>{repository.ReviewNamedEtfHolding(review,commandId);return true;},token);
}
