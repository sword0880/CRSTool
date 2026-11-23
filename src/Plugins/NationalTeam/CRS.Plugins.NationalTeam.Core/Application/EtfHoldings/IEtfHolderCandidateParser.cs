using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>冻结原件到待审候选的解析端口，不具有审核或持久化权限。</summary>
public interface IEtfHolderCandidateParser
{
    EtfHolderCandidateReport Parse(EtfHolderDocument captured,CancellationToken token);
}
