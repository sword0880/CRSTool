using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public bool EtfReportCommandComplete(Guid id)=>stores.EtfReports.CommandComplete(id);
    public EtfReportIndexSnapshot EtfReportIndex(EtfKey key)=>stores.EtfReports.Read(key);
    public void SaveEtfReportIndex(EtfKey key,DateOnly from,DateOnly through,IReadOnlyList<byte[]> pages,Guid? commandId=null)=>stores.EtfReports.Save(key,from,through,pages,commandId);
}
