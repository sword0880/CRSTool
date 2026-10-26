using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public bool EquityReportCommandComplete(Guid id) => equityStore.EquityReportCommandComplete(id);
    public void SaveEquityReports(EtfKey key, DateOnly from, DateOnly to, IReadOnlyList<byte[]> pages, Guid? commandId = null) => equityStore.SaveEquityReports(key, from, to, pages, commandId);
    public List<EquityReportDocument> EquityReports(EtfKey key) => equityStore.EquityReports(key);
}
