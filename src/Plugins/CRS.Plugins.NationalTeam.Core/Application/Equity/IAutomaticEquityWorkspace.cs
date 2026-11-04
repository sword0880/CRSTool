using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>自动持股状态的只读展示端口；桌面不执行扫描或批准金融事实。</summary>
public interface IAutomaticEquityWorkspace:IWorkspaceAccess
{
    Task<AutomaticEquityStatus> AutomaticEquityStatusAsync(CancellationToken token);
}
