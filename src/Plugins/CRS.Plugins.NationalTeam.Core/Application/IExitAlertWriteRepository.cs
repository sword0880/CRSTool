using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>预警评价、复核与通知租约写入。</summary>
public interface IExitAlertWriteRepository
{
    ExitAlertOverview EvaluateExitAlerts(bool force=false);
    void SaveAlertConfiguration(ExitAlertConfiguration value,Guid? commandId=null);
    AlertOutboxItem? ClaimAlertNotification();
    void CompleteAlertNotification(string id,string lease,AlertDispatchOutcome outcome);
    void RetryAlertNotification(string id,Guid? commandId=null);
    void RegisterAlertDocument(AlertDocumentRequest request,byte[] document,Guid? commandId=null);
    void ReviewAlertMetadata(AlertMetadataReview review,Guid? commandId=null);
    void ReviewNamedEtfHolding(NamedEtfHoldingReview review,Guid? commandId=null);
}
