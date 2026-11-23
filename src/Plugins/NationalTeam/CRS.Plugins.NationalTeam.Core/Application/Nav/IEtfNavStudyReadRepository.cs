using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>同一只读事务冻结NAV、份额、覆盖及可比性；不提供默认空输入。</summary>
public interface IEtfNavStudyReadRepository
{
    EtfNavStudyRequest FreezeEtfNavStudy(EtfKey key,DateOnly from,DateOnly through);
}
