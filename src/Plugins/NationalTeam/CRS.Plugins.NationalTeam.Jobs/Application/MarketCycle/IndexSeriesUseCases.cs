using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class IndexSeriesUseCases : MarketUseCaseScope
{
    internal IndexSeriesUseCases(MarketApplicationSession session):base(session){}
    public Task<IndexImportOutcome?> IndexSeriesReceiptAsync(Guid id,CancellationToken token)=>ReadPortAsync<IIndexSeriesReadRepository,IndexImportOutcome?>(r=>r.IndexSeriesReceipt(id),token);
    public Task<IndexImportOutcome> ImportIndexSeriesAsync(CapturedIndexPacket packet,CancellationToken token,Guid? commandId=null)=>RunAsync((IIndexSeriesWriteRepository repository)=>repository!.ImportIndexSeries(packet,commandId),token);
    public Task ReviewIndexSeriesAsync(string revision,bool accept,string reviewer,CancellationToken token,Guid? commandId=null)=>RunAsync((IIndexSeriesWriteRepository repository)=>{repository!.ReviewIndexSeries(revision,accept,reviewer,commandId);return true;},token);
    public Task<IndexCalendarSnapshot> IndexSeriesCalendarAsync(DateOnly from,DateOnly through,CancellationToken token)=>ReadPortAsync<IIndexSeriesReadRepository,IndexCalendarSnapshot>(r=>r.IndexSeriesCalendar(from,through,DateTimeOffset.UtcNow),token);
    public Task<List<IndexSeriesRevision>> IndexSeriesRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token)=>ReadPortAsync<IIndexSeriesReadRepository,List<IndexSeriesRevision>>(r=>r.IndexSeriesRevisions(from,through,DateTimeOffset.UtcNow).ToList(),token);
    /// <summary>价格、PB证明和核验日历在同一读取事务及截止时刻组合；页面取消只取消本次查询。</summary>
    public Task<IndexSeriesResearchReport> IndexSeriesReportAsync(DateOnly from,DateOnly through,CancellationToken token)=>ReadPortAsync((IIndexSeriesReadRepository r)=>
    {
        var cutoff=DateTimeOffset.UtcNow;var calendar=r.IndexSeriesCalendar(from,through,cutoff);
        return IndexSeriesResearch.Calculate(from,through,cutoff,r.IndexSeriesRevisions(from,through,cutoff),calendar.Days,calendar.Fingerprint);
    },token);
}
