// ETF官方报告目录的读取、命令确认与保存端口；目录线索不代表原件或持仓已通过审核。
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public interface IEtfReportIndexReadRepository
{
    bool EtfReportCommandComplete(Guid id,EtfKey key,DateOnly from,DateOnly through);
    EtfReportIndexSnapshot EtfReportIndex(EtfKey key);
}
public interface IEtfReportIndexWriteRepository
{
    void SaveEtfReportIndex(EtfKey key,DateOnly from,DateOnly through,IReadOnlyList<byte[]> pages,Guid? commandId=null);
}
