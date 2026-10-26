using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfUseCases
{
    public Task<List<EvidenceRow>> EvidenceAsync(EtfKey? key,DateOnly? from,DateOnly? to,CancellationToken token)
        =>ReadPortAsync<IBoundedMarketEvidence,List<EvidenceRow>>(r=>r.Evidence(key,from,to,10000),token);
    public Task<EtfDashboard?> DashboardAsync(EtfKey? key,CancellationToken token)
        =>ReadPortAsync<IEtfDashboardRepository,EtfDashboard?>(r=>r.Dashboard(key),token);
}
