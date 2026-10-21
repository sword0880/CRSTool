using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>整页只读结果在同一次仓储操作中核验；不跨刷新缓存证据结论。</summary>
public sealed record EtfDashboard(List<EtfSummary> Overview,List<EtfHistoryPoint> History,List<MarketActivityPoint> Activity);
public sealed record EtfDashboardRow(EtfSummary? Overview=null,EtfHistoryPoint? History=null,MarketActivityPoint? Activity=null);
public interface IEtfDashboardWorkspace
{
    // null 表示旧后台不支持合并读取，调用方继续使用原查询。
    Task<EtfDashboard?> DashboardAsync(EtfKey? key,CancellationToken token);
}
public interface IEtfDashboardRepository
{
    EtfDashboard Dashboard(EtfKey? key);
}
