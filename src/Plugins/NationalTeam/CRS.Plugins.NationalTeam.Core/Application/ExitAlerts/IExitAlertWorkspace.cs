using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>预警事件、规则、依据及投递的远程窄端口。</summary>
public interface IExitAlertWorkspace : IWorkspaceAccess
{
    Task<JointExitStudyReport> JointExitStudyAsync(EtfKey key,DateOnly asOf,CancellationToken token);
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
}
