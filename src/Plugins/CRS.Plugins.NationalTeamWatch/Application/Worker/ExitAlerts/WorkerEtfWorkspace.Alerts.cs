using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch;

/// <summary>桌面预警操作只走受限IPC；不打开市场库、不发送通知、不创建调度器。</summary>
public sealed partial class WorkerEtfWorkspace
{
    private async Task<List<T>> AlertRows<T>(EtfRequest query,CancellationToken token)=>(await ReadPagesAsync<T>(query,token)).Rows;
    public async Task<ExitAlertOverview> AlertOverviewAsync(CancellationToken token)=>(await ReadResearchPagesAsync<ExitAlertOverview>(new("alert-overview"),token)).Rows.Single();
    public async Task<ExitAlertConfiguration> AlertConfigurationAsync(CancellationToken token)=>(await AlertRows<ExitAlertConfiguration>(new("alert-configuration"),token)).Single();
    public Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token)=>RunAsync("alert-manage",new("alert-settings",AlertConfiguration:value),token);
    public Task<List<ExitAlertEvent>> AlertEventsAsync(ExitAlertFilter filter,CancellationToken token)=>AlertRows<ExitAlertEvent>(new("alert-events",AlertFilter:filter),token);
    public async Task<ExitAlertEvent> AlertEventAsync(string id,CancellationToken token)=>(await AlertRows<ExitAlertEvent>(new("alert-event",Revision:id),token)).Single();
    public async Task<ExitAlertEvent> AlertEventVersionAsync(string id,int sequence,CancellationToken token)=>(await AlertRows<ExitAlertEvent>(new("alert-event-version",Revision:id,Name:sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)),token)).Single();
    public Task<List<EvidenceRow>> AlertInputsAsync(string id,int? sequence,CancellationToken token)=>AlertRows<EvidenceRow>(new("alert-inputs",Revision:id,Name:sequence?.ToString(System.Globalization.CultureInfo.InvariantCulture)),token);
    public Task<List<ExitAlertChange>> AlertChangesAsync(string id,CancellationToken token)=>AlertRows<ExitAlertChange>(new("alert-changes",Revision:id),token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(CancellationToken token)=>AlertRows<AlertOutboxItem>(new("alert-outbox"),token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(ExitAlertFilter filter,CancellationToken token)=>AlertRows<AlertOutboxItem>(new("alert-outbox",AlertFilter:filter),token);
    public Task<List<AlertDeliveryRecord>> AlertDeliveriesAsync(string id,CancellationToken token)=>AlertRows<AlertDeliveryRecord>(new("alert-deliveries",Revision:id),token);
    public Task RetryAlertNotificationAsync(string id,CancellationToken token)=>RunAsync("alert-manage",new("alert-retry",Revision:id),token);
    public Task EvaluateAlertsAsync(CancellationToken token)=>RunAsync("alert-evaluate",new("alert-evaluate"),token);
    public Task<List<AlertChannel>> AlertChannelsAsync(CancellationToken token)=>AlertRows<AlertChannel>(new("alert-channels"),token);
    public Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token)=>AlertRows<AlertDocument>(new("alert-documents",key.Market,key.Ticker),token);
    public Task RegisterAlertDocumentAsync(AlertDocumentRequest request,CancellationToken token)=>RunAsync("alert-manage",new("alert-document",AlertDocument:request),token);
    public Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token)=>AlertRows<AlertFundMetadata>(new("alert-metadata"),token);
    public Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token)=>RunAsync("alert-manage",new("alert-metadata-review",AlertMetadata:review),token);
    public Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token)=>AlertRows<NamedEtfHolding>(new("alert-holdings",key.Market,key.Ticker),token);
}
