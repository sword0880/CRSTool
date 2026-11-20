using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class IndustryPeUseCases : MarketUseCaseScope
{
    internal IndustryPeUseCases(MarketApplicationSession session):base(session){}
    public Task<IndustryPeImportOutcome?> IndustryPeReceiptAsync(Guid id,CancellationToken token)=>ReadPortAsync<IIndustryPeReadRepository,IndustryPeImportOutcome?>(r=>r.IndustryPeReceipt(id),token);
    public Task<IndustryPeImportOutcome> ImportIndustryPeAsync(CapturedIndustryPeDocument document,CancellationToken token,Guid? commandId=null)=>RunAsync((IIndustryPeWriteRepository repository)=>repository!.ImportIndustryPe(document,commandId),token);
    public Task ReviewIndustryPeAsync(string revision,bool accept,string reviewer,CancellationToken token,Guid? commandId=null)=>RunAsync((IIndustryPeWriteRepository repository)=>{repository!.ReviewIndustryPe(revision,accept,reviewer,commandId);return true;},token);
    public Task<IndustryPeCalendarSnapshot> IndustryPeCalendarAsync(DateOnly from,DateOnly through,CancellationToken token)=>ReadPortAsync<IIndustryPeReadRepository,IndustryPeCalendarSnapshot>(r=>r.IndustryPeCalendar(from,through,DateTimeOffset.UtcNow),token);
    /// <summary>事实、日历及知识截止来自同一只读快照；查询取消不会停止正在联网的采集任务。</summary>
    public Task<IndustryPeReport> IndustryPeReportAsync(DateOnly from,DateOnly through,CancellationToken token)=>ReadPortAsync((IIndustryPeReadRepository r)=>
    {
        var cutoff=DateTimeOffset.UtcNow;var calendar=r.IndustryPeCalendar(from,through,cutoff);
        return IndustryPeResearch.Calculate(from,through,cutoff,r.IndustryPeRevisions(from,through,cutoff),calendar.Days,calendar.Fingerprint);
    },token);
    public Task<List<IndustryPeRevision>> IndustryPeRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token)=>ReadPortAsync<IIndustryPeReadRepository,List<IndustryPeRevision>>(r=>r.IndustryPeRevisions(from,through,DateTimeOffset.UtcNow).ToList(),token);
}
