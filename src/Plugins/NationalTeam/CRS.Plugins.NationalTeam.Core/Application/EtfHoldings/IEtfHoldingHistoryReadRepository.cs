// 具名ETF持仓历史的只读端口，按已配置证券和统计日提供已核证据及覆盖缺口。
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
public interface IEtfHoldingHistoryReadRepository
{
    NamedEtfHoldingHistoryReport EtfHoldingHistory(EtfKey key,DateOnly asOf);
}
