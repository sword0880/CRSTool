using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>有限诊断只含阶段、耗时和行数；不是金融事实，不改变原查询、知识时点或取消边界。</summary>
public sealed record EtfQueryStage(string Name,double ElapsedMs,int Items);
public sealed record EtfDashboardQueryProfile(EtfDashboard Dashboard,IReadOnlyList<EtfQueryStage> Stages);
public interface IEtfDashboardProfileRepository
{
    EtfDashboardQueryProfile ProfileDashboard(EtfKey? key);
}

public sealed partial class EtfUseCases
{
    // 可选诊断能力不加入必需仓储合同；旧提供方明确不支持，不能以空仪表盘替代。
    internal Task<EtfDashboardQueryProfile> ProfileDashboardAsync(EtfKey? key,CancellationToken token)
        =>ReadPortAsync<IEtfDashboardProfileRepository,EtfDashboardQueryProfile>(current=>current.ProfileDashboard(key),token);
}
