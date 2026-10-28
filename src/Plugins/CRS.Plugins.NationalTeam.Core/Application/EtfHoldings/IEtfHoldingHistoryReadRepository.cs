using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
public interface IEtfHoldingHistoryReadRepository
{
    NamedEtfHoldingHistoryReport EtfHoldingHistory(EtfKey key,DateOnly asOf);
}
