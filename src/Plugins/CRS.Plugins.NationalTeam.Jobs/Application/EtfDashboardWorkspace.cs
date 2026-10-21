using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfWorkspace : IEtfDashboardWorkspace
{
    public Task<EtfDashboard?> DashboardAsync(EtfKey? key,CancellationToken token)=>RunAsync<EtfDashboard?>(()=>
        repository is IEtfDashboardRepository dashboard?dashboard.Dashboard(key):null,token);
}
