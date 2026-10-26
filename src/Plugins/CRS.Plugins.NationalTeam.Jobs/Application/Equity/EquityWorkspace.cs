using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfWorkspace
{
    // 写入用例只依赖本模块端口，避免扩大对其他业务仓储的依赖。
    private IEquityWriteRepository EquityWrites=>repository!;
    private IEquityReadRepository EquityReads=>repository!;

    public Task<List<EquityWatch>> EquityWatchlistAsync(CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityWatchlist(),token);
    public Task<List<EquityHoldingRow>> EquityHoldingsAsync(string? search,string? institution,string? nature,bool favorites,CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityHoldings(search,institution,nature,favorites),token);
    public Task<List<EquityDisclosure>> EquityDisclosuresAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityDisclosures(key),token);
    public Task<List<EquityHoldingRow>> EquityEventsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityEvents(key),token);
    public Task<List<EquityReportDocument>> EquityReportsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityReports(key),token);
    public Task SaveEquityReportsAsync(EtfKey key,DateOnly from,DateOnly to,IReadOnlyList<byte[]> pages,CancellationToken token,Guid? commandId)=>RunAsync(()=>{EquityWrites.SaveEquityReports(key,from,to,pages,commandId);return true;},token);
    public Task<bool> EquityReportCommandCompleteAsync(Guid id,CancellationToken token)=>RunAsync(()=>EquityReads.EquityReportCommandComplete(id),token);
    public Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token)=>SaveEquityWatchAsync(watch,token,null);
    public Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token,Guid? commandId)=>RunAsync(()=>{EquityWrites.SaveEquityWatch(watch,commandId);return true;},token);
    public Task SaveEquityDisclosureAsync(string market,string ticker,Uri uri,byte[] bytes,CancellationToken token)=>RunAsync(()=>{EquityWrites.SaveEquityDisclosure(market,ticker,uri,bytes);return true;},token);
    public Task SyncEquityAsync(CancellationToken token)=>throw new InvalidOperationException("采集由Worker任务执行。");
    public Task ReviewEquityAsync(EquityReview review,CancellationToken token)=>throw new InvalidOperationException("PDF下载与核验由Worker任务执行。");
    public Task ReviewEquityDocumentAsync(EquityReview review,byte[] document,CancellationToken token,Guid? commandId)=>RunAsync(()=>{EquityWrites.ReviewEquity(review,document,commandId);return true;},token);
}
