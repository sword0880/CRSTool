// 联合退出观察的只读仓储端口，按证券和统计日取得冻结研究结果，业务判断由研究模块完成。
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
public interface IJointExitStudyReadRepository
{
    JointExitStudyReport JointExitStudy(EtfKey key,DateOnly asOf);
}
