using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for NAV observations and immutable revision history.</summary>
public interface INavWorkspace : IWorkspaceAccess
{
    Task<List<NavAcceptedPoint>> NavPointsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token);
    Task<List<NavRevision>> NavRevisionsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token);
    Task<EtfNavStudyReport> NavStudyAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token);
}
