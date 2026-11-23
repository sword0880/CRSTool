// 界面工作区的访问状态合同；可管理标志用于交互提示，正式写入权限仍由后台核验。
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Cross-feature UI capability; authorization remains enforced by the Worker, not by this display hint.</summary>
public interface IWorkspaceAccess
{
    bool CanManage { get; }
}
