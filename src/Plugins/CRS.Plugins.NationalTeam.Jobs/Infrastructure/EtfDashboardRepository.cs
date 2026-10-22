using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public EtfDashboard Dashboard(EtfKey? key)
    {
        // 延迟只读事务，不为展示页抢占写锁；仅核验本页实际依赖的份额、成交及日历原件。
        using var transaction=db.BeginTransaction(deferred:true);
        ValidateEvidence(dashboardOnly:true);
        var result=new EtfDashboard(ReadOverview(),ReadHistory(key),ReadMarketActivity());
        operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
        return result;
    }
}
