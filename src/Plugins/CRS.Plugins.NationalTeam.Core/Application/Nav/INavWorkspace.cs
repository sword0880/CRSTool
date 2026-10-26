using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for NAV observations and immutable revision history.</summary>
public interface INavWorkspace : IWorkspaceAccess
{
    Task<List<NavAcceptedPoint>> NavPointsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token) => throw new NotSupportedException("NAV查询需要更新后台服务。");
    Task<List<NavRevision>> NavRevisionsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token) => throw new NotSupportedException("NAV修订需要更新后台服务。");
}
