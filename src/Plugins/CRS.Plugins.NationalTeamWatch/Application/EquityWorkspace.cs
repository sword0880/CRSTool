using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfWorkspace
{
    public Task<List<EquityWatch>> EquityWatchlistAsync(CancellationToken token)=>RunAsync(()=>repository!.EquityWatchlist(),token);
    public Task<List<EquityHoldingRow>> EquityHoldingsAsync(string? search,string? institution,string? nature,bool favorites,CancellationToken token)=>RunAsync(()=>repository!.EquityHoldings(search,institution,nature,favorites),token);
    public Task<List<EquityDisclosure>> EquityDisclosuresAsync(EtfKey key,CancellationToken token)=>RunAsync(()=>repository!.EquityDisclosures(key),token);
    public Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token)=>SaveEquityWatchAsync(watch,token,null);
    public Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token,Guid? commandId)=>RunAsync(()=>{repository!.SaveEquityWatch(watch,commandId);return true;},token);
    public Task SaveEquityDisclosureAsync(string market,string ticker,Uri uri,byte[] bytes,CancellationToken token)=>RunAsync(()=>{repository!.SaveEquityDisclosure(market,ticker,uri,bytes);return true;},token);
    public Task SyncEquityAsync(CancellationToken token)=>throw new InvalidOperationException("采集由Worker任务执行。");
    public Task ReviewEquityAsync(EquityReview review,CancellationToken token)=>throw new InvalidOperationException("PDF下载与核验由Worker任务执行。");
    public Task ReviewEquityDocumentAsync(EquityReview review,byte[] document,CancellationToken token,Guid? commandId)=>RunAsync(()=>{repository!.ReviewEquity(review,document,commandId);return true;},token);
}
