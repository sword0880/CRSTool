using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public interface IEtfReportIndexReadRepository
{
    bool EtfReportCommandComplete(Guid id);
    EtfReportIndexSnapshot EtfReportIndex(EtfKey key);
}
public interface IEtfReportIndexWriteRepository
{
    void SaveEtfReportIndex(EtfKey key,DateOnly from,DateOnly through,IReadOnlyList<byte[]> pages,Guid? commandId=null);
}
