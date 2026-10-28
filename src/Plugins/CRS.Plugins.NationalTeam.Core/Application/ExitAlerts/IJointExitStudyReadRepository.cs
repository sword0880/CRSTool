using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
public interface IJointExitStudyReadRepository
{
    JointExitStudyReport JointExitStudy(EtfKey key,DateOnly asOf);
}
