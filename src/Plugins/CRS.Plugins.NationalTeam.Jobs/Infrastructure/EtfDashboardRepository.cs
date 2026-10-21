using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public EtfDashboard Dashboard(EtfKey? key)
    {
        // 同一连接、同一读事务；全部原件核验一次，后续查询不会混入并发提交。
        using var transaction=db.BeginTransaction();
        ValidateEvidence();
        var result=new EtfDashboard(ReadOverview(),ReadHistory(key),ReadMarketActivity());
        operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
        return result;
    }
}
