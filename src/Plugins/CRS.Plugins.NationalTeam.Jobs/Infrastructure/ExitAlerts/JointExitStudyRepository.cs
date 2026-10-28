using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
public sealed partial class MarketRepository
{
    public JointExitStudyReport JointExitStudy(EtfKey key,DateOnly asOf)=>stores.JointExit.Read(key,asOf);
}
