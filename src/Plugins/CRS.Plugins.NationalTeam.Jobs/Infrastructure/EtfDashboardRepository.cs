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
        // 概览与历史在同一只读事务内复用已读取的份额列表；不跨查询缓存金融值或原件核验结论。
        var keys=Configuration().Where(x=>x.Active).Select(x=>new EtfKey(x.Market,x.Ticker));
        if(key is not null)keys=keys.Append(key);
        var points=keys.Distinct().ToDictionary(x=>x,ReadPoints);
        return new(ReadOverview(points),ReadHistory(key,points),ReadMarketActivity());
    }
}
