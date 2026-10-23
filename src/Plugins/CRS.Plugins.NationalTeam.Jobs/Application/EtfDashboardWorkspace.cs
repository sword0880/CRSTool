using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfWorkspace : IEtfDashboardWorkspace
{
    public Task<EtfDashboard?> DashboardAsync(EtfKey? key,CancellationToken token)=>ReadAsync<EtfDashboard?>(current=>
        current is IEtfDashboardRepository dashboard?dashboard.Dashboard(key):null,token);
}
