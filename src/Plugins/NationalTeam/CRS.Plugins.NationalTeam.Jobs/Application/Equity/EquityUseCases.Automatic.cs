using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EquityUseCases
{
    public Task<AutomaticEquityStatus> AutomaticEquityStatusAsync(CancellationToken token)=>ReadPortAsync((IAutomaticEquityReadRepository r)=>r.AutomaticEquityStatus(),token);
    public Task<List<AutomaticEquityDocument>> PendingAutomaticEquityDocumentsAsync(int count,CancellationToken token)=>ReadPortAsync((IAutomaticEquityReadRepository r)=>r.PendingAutomaticEquityDocuments(count),token);
    public Task SaveAutomaticEquitySecuritiesAsync(byte[] original,CancellationToken token)=>RunAsync((IAutomaticEquityWriteRepository r)=>{r.SaveAutomaticEquitySecurities(original);return true;},token);
    public Task BeginAutomaticEquityWindowAsync(DateOnly through,CancellationToken token)=>RunAsync((IAutomaticEquityWriteRepository r)=>{r.BeginAutomaticEquityWindow(through);return true;},token);
    public Task SaveAutomaticEquityPageAsync(byte[] original,CancellationToken token)=>RunAsync((IAutomaticEquityWriteRepository r)=>{r.SaveAutomaticEquityPage(original);return true;},token);
    public Task<string> ImportAutomaticEquityDocumentAsync(string id,byte[] original,CancellationToken token)=>RunAsync((IAutomaticEquityWriteRepository r)=>r.ImportAutomaticEquityDocument(id,original),token);
    public Task RecordAutomaticEquityIssueAsync(string? id,string reason,byte[]? original,CancellationToken token)=>RunAsync((IAutomaticEquityWriteRepository r)=>{r.RecordAutomaticEquityIssue(id,reason,original);return true;},token);
}
