namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Cross-feature UI capability; authorization remains enforced by the Worker, not by this display hint.</summary>
public interface IWorkspaceAccess
{
    bool CanManage => true;
}
