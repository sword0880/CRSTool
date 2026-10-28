using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
public sealed partial class MarketRepository
{
    public NamedEtfHoldingHistoryReport EtfHoldingHistory(EtfKey key,DateOnly asOf)=>stores.HoldingHistory.Read(key,asOf);
}
