using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for exit-alert evaluation, review, evidence, and delivery workflows.</summary>
public interface IExitAlertWorkspace : IWorkspaceAccess
{
    Task<ExitAlertOverview> AlertOverviewAsync(CancellationToken token);
    Task<ExitAlertConfiguration> AlertConfigurationAsync(CancellationToken token);
    Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token);
    Task<List<ExitAlertEvent>> AlertEventsAsync(ExitAlertFilter filter,CancellationToken token);
    Task<ExitAlertEvent> AlertEventAsync(string id,CancellationToken token);
    Task<ExitAlertEvent> AlertEventVersionAsync(string id,int sequence,CancellationToken token);
    Task<List<EvidenceRow>> AlertInputsAsync(string id,int? sequence,CancellationToken token);
    Task<List<ExitAlertChange>> AlertChangesAsync(string id,CancellationToken token);
    Task<List<AlertOutboxItem>> AlertOutboxAsync(CancellationToken token);
    Task<List<AlertOutboxItem>> AlertOutboxAsync(ExitAlertFilter filter,CancellationToken token);
    Task<List<AlertDeliveryRecord>> AlertDeliveriesAsync(string id,CancellationToken token);
    Task RetryAlertNotificationAsync(string id,CancellationToken token);
    Task EvaluateAlertsAsync(CancellationToken token);
    Task<List<AlertChannel>> AlertChannelsAsync(CancellationToken token);
    Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token);
    Task RegisterAlertDocumentAsync(AlertDocumentRequest request,CancellationToken token);
    Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token);
    Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token);
    Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token);
    Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token);
}
