using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>与正常验收完全相同的只读路径，仅附加有限的阶段耗时和计数。</summary>
public sealed record EtfAcceptanceQueryProfile(IReadOnlyList<EtfAcceptance> Acceptance,IReadOnlyList<EtfQueryStage> Stages);
public interface IEtfAcceptanceProfileRepository
{
    EtfAcceptanceQueryProfile ProfileAcceptance(EtfKey? key,DateOnly through);
}

public sealed partial class EtfWorkspace
{
    internal Task<EtfAcceptanceQueryProfile> ProfileAcceptanceAsync(EtfKey? key,DateOnly through,CancellationToken token)
        =>ReadPortAsync<IEtfAcceptanceProfileRepository,EtfAcceptanceQueryProfile>(current=>current.ProfileAcceptance(key,through),token);
}
