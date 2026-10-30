using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>预警事件、版本、依据及通知记录读取。</summary>
public interface IExitAlertReadRepository : IJointExitStudyReadRepository
{
    ExitAlertOverview AlertOverview();
    ExitAlertConfiguration AlertConfiguration();
    List<ExitAlertEvent> AlertEvents(ExitAlertFilter filter);
    ExitAlertEvent? AlertEvent(string id);
    ExitAlertEvent? AlertEvent(string id,int? sequence);
    List<EvidenceRow> AlertInputs(string id,int? sequence=null);
    List<ExitAlertChange> AlertChanges(string id);
    List<AlertOutboxItem> AlertOutbox(ExitAlertFilter? filter=null);
    List<AlertDeliveryRecord> AlertDeliveries(string id);
}
