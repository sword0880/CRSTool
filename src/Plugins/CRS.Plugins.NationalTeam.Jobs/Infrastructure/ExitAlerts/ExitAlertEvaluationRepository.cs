using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    // 聚合入口只转发；预警实现、事务及状态由独立模块负责。
    public ExitAlertOverview EvaluateExitAlerts(bool force=false) => alertStore.EvaluateExitAlerts(force);
    public ExitAlertOverview AlertOverview() => alertStore.AlertOverview();
}
