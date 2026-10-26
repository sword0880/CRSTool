using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    // 聚合入口只转发；预警实现、事务及状态由独立模块负责。
    public AlertOutboxItem? ClaimAlertNotification() => alertStore.ClaimAlertNotification();
    public ExitAlertEvent? AlertEvent(string id) => alertStore.AlertEvent(id);
    public ExitAlertEvent? AlertEvent(string id,int? sequence) => alertStore.AlertEvent(id, sequence);
    public List<CRS.Plugins.NationalTeamWatch.Application.EvidenceRow> AlertInputs(string id,int? sequence=null) => alertStore.AlertInputs(id, sequence);
    public void CompleteAlertNotification(string id,string lease,AlertDispatchOutcome outcome) => alertStore.CompleteAlertNotification(id, lease, outcome);
    public List<AlertDeliveryRecord> AlertDeliveries(string id) => alertStore.AlertDeliveries(id);
    public void RetryAlertNotification(string id,Guid? commandId=null) => alertStore.RetryAlertNotification(id, commandId);
}
