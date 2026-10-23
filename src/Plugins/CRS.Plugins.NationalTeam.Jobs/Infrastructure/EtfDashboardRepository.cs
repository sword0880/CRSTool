using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public EtfDashboard Dashboard(EtfKey? key)
    {
        if(readSnapshot)return ReadDashboard(key);
        // 延迟只读事务，不为展示页抢占写锁；仅核验本页实际依赖的份额、成交及日历原件。
        using var transaction=db.BeginTransaction(deferred:true);
        var result=ReadDashboard(key);
        operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
        return result;
    }
    private EtfDashboard ReadDashboard(EtfKey? key)
    {
        ValidateEvidence(dashboardOnly:true);
        return new(ReadOverview(),ReadHistory(key),ReadMarketActivity());
    }
}
