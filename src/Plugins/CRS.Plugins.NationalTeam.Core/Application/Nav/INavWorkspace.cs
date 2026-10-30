// NAV功能的远程读取与采集合同，按证券和日期请求后台资料，调用方显式传递取消。
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>Narrow V2 port for NAV observations and immutable revision history.</summary>
public interface INavWorkspace : IWorkspaceAccess
{
    Task<List<NavAcceptedPoint>> NavPointsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token);
    Task<List<NavRevision>> NavRevisionsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token);
    Task<EtfNavStudyReport> NavStudyAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token);
}
