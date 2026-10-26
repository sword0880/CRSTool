using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public EtfNavStudyRequest FreezeEtfNavStudy(EtfKey key,DateOnly from,DateOnly through)=>stores.NavStudy.Freeze(key,from,through);
}
