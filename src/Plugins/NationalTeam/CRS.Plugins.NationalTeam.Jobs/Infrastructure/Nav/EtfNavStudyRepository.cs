// ETF份额与NAV联合研究的仓储转发入口，在同一只读快照中冻结各模块输入。
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public EtfNavStudyRequest FreezeEtfNavStudy(EtfKey key,DateOnly from,DateOnly through)=>stores.NavStudy.Freeze(key,from,through);
}
