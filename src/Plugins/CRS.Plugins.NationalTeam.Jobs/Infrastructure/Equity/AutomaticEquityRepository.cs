using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository:IAutomaticEquityReadRepository,IAutomaticEquityWriteRepository
{
    public AutomaticEquityStatus AutomaticEquityStatus()=>equityStore.AutomaticEquityStatus();
    public List<AutomaticEquityDocument> PendingAutomaticEquityDocuments(int count)=>equityStore.PendingAutomaticEquityDocuments(count);
    public void SaveAutomaticEquitySecurities(byte[] original)=>equityStore.SaveAutomaticEquitySecurities(original);
    public void BeginAutomaticEquityWindow(DateOnly through)=>equityStore.BeginAutomaticEquityWindow(through);
    public void SaveAutomaticEquityPage(byte[] original)=>equityStore.SaveAutomaticEquityPage(original);
    public string ImportAutomaticEquityDocument(string id,byte[] original)=>equityStore.ImportAutomaticEquityDocument(id,original);
    public void RecordAutomaticEquityIssue(string? id,string reason,byte[]? original=null)=>equityStore.RecordAutomaticEquityIssue(id,reason,original);
}
