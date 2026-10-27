using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EquityUseCases : MarketUseCaseScope
{
    internal EquityUseCases(MarketApplicationSession session):base(session){}

    public Task<List<EquityWatch>> EquityWatchlistAsync(CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityWatchlist(),token);
    public Task<List<EquityHoldingRow>> EquityHoldingsAsync(string? search,string? institution,string? nature,bool favorites,CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityHoldings(search,institution,nature,favorites),token);
    public Task<List<EquityDisclosure>> EquityDisclosuresAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityDisclosures(key),token);
    public Task<List<EquityHoldingRow>> EquityEventsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityEvents(key),token);
    public Task<List<EquityReportDocument>> EquityReportsAsync(EtfKey key,CancellationToken token)=>ReadPortAsync((IEquityReadRepository current)=>current.EquityReports(key),token);
    public Task SaveEquityReportsAsync(EtfKey key,DateOnly from,DateOnly to,IReadOnlyList<byte[]> pages,CancellationToken token,Guid? commandId)=>RunAsync((IEquityWriteRepository repository)=>{repository.SaveEquityReports(key,from,to,pages,commandId);return true;},token);
    public Task<bool> EquityReportCommandCompleteAsync(Guid id,CancellationToken token)=>ReadPortAsync((IEquityReadRepository r)=>r.EquityReportCommandComplete(id),token);
    public Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token)=>SaveEquityWatchAsync(watch,token,null);
    public Task SaveEquityWatchAsync(EquityWatch watch,CancellationToken token,Guid? commandId)=>RunAsync((IEquityWriteRepository repository)=>{repository.SaveEquityWatch(watch,commandId);return true;},token);
    public Task SaveEquityDisclosureAsync(string market,string ticker,Uri uri,byte[] bytes,CancellationToken token)=>RunAsync((IEquityWriteRepository repository)=>{repository.SaveEquityDisclosure(market,ticker,uri,bytes);return true;},token);
    public Task ReviewEquityDocumentAsync(EquityReview review,byte[] document,CancellationToken token,Guid? commandId)=>RunAsync((IEquityWriteRepository repository)=>{repository.ReviewEquity(review,document,commandId);return true;},token);
    public Task<EquityDirectionReport> EquityDirectionAsync(DateOnly? reportAsOf,CancellationToken token)=>ReadPortAsync((IEquityReadRepository repository)=>repository.EquityDirection(reportAsOf),token);
    public Task ReviewEquityDirectionAsync(EquityDirectionReview review,CancellationToken token,Guid commandId)=>RunAsync((IEquityWriteRepository repository)=>{repository.ReviewEquityDirection(review,commandId);return true;},token);
}
