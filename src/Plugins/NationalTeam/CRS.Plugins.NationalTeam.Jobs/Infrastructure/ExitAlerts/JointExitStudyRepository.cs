// 联合退出观察的只读仓储转发，组合研究与正式预警通知的写入职责分离。
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
public sealed partial class MarketRepository
{
    public JointExitStudyReport JointExitStudy(EtfKey key,DateOnly asOf)=>stores.JointExit.Read(key,asOf);
}
