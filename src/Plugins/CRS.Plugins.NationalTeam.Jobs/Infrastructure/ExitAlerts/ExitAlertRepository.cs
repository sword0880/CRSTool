using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    // 聚合入口只转发；预警实现、事务及状态由独立模块负责。
    private void InitializeExitAlertSchema() => alertStore.InitializeExitAlertSchema();
    private void MarkAlertPending() => alertStore.MarkAlertPending();
    public ExitAlertConfiguration AlertConfiguration() => alertStore.AlertConfiguration();
    public void SaveAlertConfiguration(ExitAlertConfiguration value,Guid? commandId=null) => alertStore.SaveAlertConfiguration(value, commandId);
    public List<ExitAlertEvent> AlertEvents(ExitAlertFilter filter) => alertStore.AlertEvents(filter);
    public List<ExitAlertChange> AlertChanges(string id) => alertStore.AlertChanges(id);
    public List<AlertOutboxItem> AlertOutbox(ExitAlertFilter? filter=null) => alertStore.AlertOutbox(filter);
}
