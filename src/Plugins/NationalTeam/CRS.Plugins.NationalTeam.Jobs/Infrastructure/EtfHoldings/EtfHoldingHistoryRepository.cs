// 具名ETF持仓历史的仓储转发，沿用一致性快照及已核证据，不在聚合入口计算跨期结论。
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
public sealed partial class MarketRepository
{
    public NamedEtfHoldingHistoryReport EtfHoldingHistory(EtfKey key,DateOnly asOf)=>stores.HoldingHistory.Read(key,asOf);
}
