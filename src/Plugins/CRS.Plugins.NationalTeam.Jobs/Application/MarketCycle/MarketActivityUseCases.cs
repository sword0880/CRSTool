using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class MarketActivityUseCases : MarketUseCaseScope
{
    internal MarketActivityUseCases(MarketApplicationSession session):base(session){}

    private IMarketActivitySource? activitySource;
    public void SetMarketActivitySource(IMarketActivitySource source)=>activitySource=source;
    public Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token)=>ReadPortAsync((IMarketActivityReadRepository current)=>current.MarketActivity(),token);
    public Task<List<MarketActivityRevision>> MarketActivityRevisionsAsync(CancellationToken token)=>ReadPortAsync((IMarketActivityReadRepository current)=>current.MarketActivityRevisions(),token);
    public Task<MarketActivityKnowledgeReport> MarketActivityKnowledgeAsync(DateOnly from,DateOnly through,DateTimeOffset? cutoff,CancellationToken token)
        =>ReadPortAsync((IMarketActivityReadRepository current)=>current.MarketActivityKnowledge(from,through,cutoff),token);
    public Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token)=>ReviewMarketActivityAsync(id,accept,reviewer,token,null);
    public Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token,Guid? commandId)=>RunAsync((IMarketActivityWriteRepository repository)=>{repository.ReviewMarketActivity(id,accept,reviewer,commandId);return true;},token);
    public async Task<MarketActivitySyncReport> SyncRecentMarketActivityAsync(CancellationToken token)
    {
        var now=CRS.Timing.BeijingClock.Now;
        var today=DateOnly.FromDateTime(now.DateTime);
        var eligible=AutomaticCollectionWindow.MarketActivity(now);
        var cursor=await ReadPortAsync((IMarketActivityReadRepository repository)=>repository.ReadMarketActivityCursor(),token);
        var from=cursor?.AddDays(-7)??today.AddDays(-35);var to=from.AddDays(29)<eligible?from.AddDays(29):eligible;
        var result=await SyncMarketActivityRangeAsync(from,to,token,true);
        return result with{HasMore=result.HasMore || to<eligible};
    }
    public async Task SyncMarketActivityAsync(DateOnly from,DateOnly to,CancellationToken token)
    {
        var result=await SyncMarketActivityRangeAsync(from,to,token,false);
        if(result.SourceIssues>0)throw new InvalidDataException("部分市场成交统计未取得，两市不完整日期不生成总额。请在任务中心查看并重试。");
    }
    public Task<MarketActivitySyncReport> SyncMarketActivityReportAsync(DateOnly from,DateOnly to,CancellationToken token)=>SyncMarketActivityRangeAsync(from,to,token,false);
    private async Task<MarketActivitySyncReport> SyncMarketActivityRangeAsync(DateOnly from,DateOnly to,CancellationToken token,bool track)
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
        if(from>to || to>today || to.DayNumber-from.DayNumber>400)throw new InvalidDataException("采集区间必须不超过400个自然日，不能包含未来日期。");
        if(activitySource is null)throw new InvalidOperationException("成交统计来源未注册。");
        // 手动历史请求按事实续采；自动近段采集保留原有的官方修订发现能力。
        var existing=track?new HashSet<(string Market,DateOnly Date)>():await ReadPortAsync((IMarketActivityReadRepository repository)=>repository.VerifiedMarketActivityDates(from,to),token);
        var downloaded=new HashSet<(string Market,DateOnly Date)>();
        int issues=0,received=0,days=0,sourceCalls=0,closed=0,empty=0,skipped=0,deferred=0;
        string? firstFailure=null,firstMarket=null;DateOnly? firstDate=null;
        var contiguous=true;var blocked=new Dictionary<string,(DateTimeOffset? Until,DateOnly Date)>();
        var daily=new Dictionary<DateOnly,CollectionDay>();var nextCursor=from;
        var retryByMarket=new Dictionary<string,DateTimeOffset>();
        DateTimeOffset? retryAt=null;
        MarketActivitySyncReport Result(bool more)=>new(days,issues,received,sourceCalls,closed,empty,firstFailure,more,skipped,retryAt,firstDate,firstMarket);
        void Failure(string code,DateOnly day,string market,DateTimeOffset? until=null)
        {
            issues++;
            // 并行完成顺序不能改变“首个失败”的日期及同日沪深排序。
            if(firstDate is null||day<firstDate||day==firstDate&&market=="SSE"&&firstMarket=="SZSE")
            {firstFailure=code;firstDate=day;firstMarket=market;}
            var next=until??CRS.Timing.BeijingClock.Now.AddMinutes(5);
            if(!retryByMarket.TryGetValue(market,out var prior)||next>prior)retryByMarket[market]=next;
            retryAt=retryByMarket.Values.Min();
        }
        await foreach(var fetched in DownloadActivityDaysAsync(from,to,existing,token))
        {
            var day=fetched.Day;var market=fetched.Market;var trading=fetched.Trading;
            var beforeReceived=received;var beforeIssues=issues;var beforeClosed=closed;
            var beforeEmpty=empty;var beforeSkipped=skipped;var beforeDeferred=deferred;
            token.ThrowIfCancellationRequested();
            if(fetched.State=="Closed")
            {
                closed++;await RunAsync((IMarketActivityWriteRepository repository)=>{repository.SaveMarketActivity(market,day,null);return true;},token);
                await ReportAsync(new("MarketClosed",day,market));
            }
            else if(fetched.State=="Reused")
            {skipped++;await ReportAsync(new("MarketReused",day,market));}
            else if(fetched.State=="Deferred")deferred++;
            else
            {
                sourceCalls++;var source=fetched.Source;
                if(fetched.Error is {} error)
                {
                    var (failureCode,until)=ActivityFailure(error);
                    Failure(failureCode,day,market,until);
                    if(BlocksActivitySource(failureCode,until))blocked[market]=(until,day);
                    await ReportAsync(new("SourceFailed",day,market));
                    if(blocked.ContainsKey(market))await ReportAsync(new("SourceCoolingDown",day,market,Until:until));
                }
                await RunAsync((IMarketActivityWriteRepository repository)=>{repository.SaveMarketActivity(market,day,source);return true;},token);
                if(source is not null)
                {
                    if(source.Observation is not null){received++;downloaded.Add((market,day));}
                    else
                    {
                        empty++;
                        if(trading==true)
                        {
                            var now=CRS.Timing.BeijingClock.Now;
                            Failure("OpenDayNoOfficialData",day,market,day==DateOnly.FromDateTime(now.DateTime)?now.AddMinutes(30):null);
                        }
                        await ReportAsync(new("MarketNoData",day,market));
                    }
                    await ReportAsync(new("SourceParsed",day,market,Received:source.Observation is null?0:1));
                }
            }
            if(!daily.TryGetValue(day,out var summary))daily[day]=summary=new();
            summary.Add(received-beforeReceived,skipped-beforeSkipped,closed-beforeClosed,empty-beforeEmpty,deferred-beforeDeferred,issues-beforeIssues);
            if(summary.Completed==2)
            {
                days++;await ReportAsync(summary.Progress(day));
                // 日期可乱序下载和提交，但游标只能越过两市都结束且没有缺口的连续前缀。
                while(nextCursor<=to&&daily.TryGetValue(nextCursor,out var ready)&&ready.Completed==2)
                {
                    if(ready.Issues>0||ready.Deferred>0)contiguous=false;
                    var committedDay=nextCursor;
                    if(track&&contiguous)await RunAsync((IMarketActivityWriteRepository repository)=>{repository.RecordMarketActivityCursor(committedDay);return true;},token);
                    nextCursor=nextCursor.AddDays(1);
                }
            }
        }
        // 冲突/损坏事实即使重新下载了同值也不能冒充已核验完成。
        var verified=await ReadPortAsync((IMarketActivityReadRepository repository)=>repository.VerifiedMarketActivityDates(from,to),token);
        foreach(var missing in downloaded.Except(verified))Failure("ObservationUnverified",missing.Date,missing.Market);
        await ReportAsync(new("MarketRangeCompleted",to,Received:received){Reused=skipped,Closed=closed,Empty=empty,Deferred=deferred,Issues=issues});
        if(blocked.Count>0)
        {
            var last=blocked.OrderBy(x=>x.Value.Until??retryAt).First();await ReportAsync(new("SourceCoolingDown",last.Value.Date,last.Key,Until:retryAt));
        }
        return Result(blocked.Count>0);
    }
}
