using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository:IEtfDashboardProfileRepository
{
    private List<EtfQueryStage>? dashboardStages;
    public EtfDashboardQueryProfile ProfileDashboard(EtfKey? key)
    {
        if(dashboardStages is not null)throw new InvalidOperationException("同一读取会话不能嵌套诊断。");
        dashboardStages=[];
        try{return new(Dashboard(key),dashboardStages.ToArray());}
        finally{dashboardStages=null;}
    }
    // 每个只读会话由原有读取门槛独占；失败/取消后清空阶段，不跨查询缓存原件或金融结果。
    private void RecordDashboardStage(string name,long started,int items)
        =>dashboardStages?.Add(new(name,System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,items));
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
        var started=System.Diagnostics.Stopwatch.GetTimestamp();
        var keys=Configuration().Where(x=>x.Active).Select(x=>new EtfKey(x.Market,x.Ticker));
        if(key is not null)keys=keys.Append(key);
        var points=ReadDashboardPoints(keys.Distinct());
        RecordDashboardStage("PointsSql",started,points.Sum(x=>x.Value.Count));started=System.Diagnostics.Stopwatch.GetTimestamp();
        var overview=ReadOverview(points);RecordDashboardStage("OverviewSqlAndProjection",started,overview.Count);started=System.Diagnostics.Stopwatch.GetTimestamp();
        var history=ReadHistory(key,points);RecordDashboardStage("HistorySqlAndProjection",started,history.Count);started=System.Diagnostics.Stopwatch.GetTimestamp();
        var activity=ReadMarketActivity();RecordDashboardStage("ActivitySqlAndProjection",started,activity.Count);
        return new(overview,history,activity);
    }
}
