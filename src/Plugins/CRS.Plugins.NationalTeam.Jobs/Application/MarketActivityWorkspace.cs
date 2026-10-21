namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfWorkspace
{
    private IMarketActivitySource? activitySource;
    public void SetMarketActivitySource(IMarketActivitySource source)=>activitySource=source;
    public Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token)=>RunAsync(()=>repository!.MarketActivity(),token);
    public Task<List<MarketActivityRevision>> MarketActivityRevisionsAsync(CancellationToken token)=>RunAsync(()=>repository!.MarketActivityRevisions(),token);
    public Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token)=>ReviewMarketActivityAsync(id,accept,reviewer,token,null);
    public Task ReviewMarketActivityAsync(string id,bool accept,string reviewer,CancellationToken token,Guid? commandId)=>RunAsync(()=>{repository!.ReviewMarketActivity(id,accept,reviewer,commandId);return true;},token);
    public async Task<MarketActivitySyncReport> SyncRecentMarketActivityAsync(CancellationToken token)
    {
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        var cursor=await RunAsync(()=>repository!.ReadMarketActivityCursor(),token);
        var from=cursor?.AddDays(-7)??today.AddDays(-35);var to=from.AddDays(29)<today?from.AddDays(29):today;
        var result=await SyncMarketActivityRangeAsync(from,to,token,true);
        return result with{HasMore=to<today};
    }
    public async Task SyncMarketActivityAsync(DateOnly from,DateOnly to,CancellationToken token)
    {
        var result=await SyncMarketActivityRangeAsync(from,to,token,false);
        if(result.SourceIssues>0)throw new InvalidDataException("部分市场成交统计未取得，两市不完整日期不生成总额。请在任务中心查看并重试。");
    }
    public Task<MarketActivitySyncReport> SyncMarketActivityReportAsync(DateOnly from,DateOnly to,CancellationToken token)=>SyncMarketActivityRangeAsync(from,to,token,false);
    private async Task<MarketActivitySyncReport> SyncMarketActivityRangeAsync(DateOnly from,DateOnly to,CancellationToken token,bool track)
    {
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        if(from>to || to>today || to.DayNumber-from.DayNumber>400)throw new InvalidDataException("采集区间必须不超过400个自然日，不能包含未来日期。");
        if(activitySource is null)throw new InvalidOperationException("成交统计来源未注册。");
        int issues=0,received=0,days=0,sourceCalls=0,closed=0,empty=0;string? firstFailure=null;var contiguous=true;
        for(var day=from;day<=to;day=day.AddDays(1))
        {
            token.ThrowIfCancellationRequested();await ReportAsync(new("DayStarted",day));
            foreach(var market in new[]{"SSE","SZSE"})
            {
                var trading=await RunAsync(()=>repository!.IsReviewedTradingDay(market,day),token);
                if(trading==false){closed++;await RunAsync(()=>{repository!.SaveMarketActivity(market,day,null);return true;},token);continue;}
                await ReportAsync(new("SourceStarted",day,market));MarketActivitySource? source=null;sourceCalls++;
                try {source=await activitySource.FetchAsync(market,day,token);}
                catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                catch(Exception error)
                {
                    // 错误只输出有限分类，不将任意官网正文、请求地址或隐私数据写入执行检查点。
                    firstFailure??=error switch
                    {
                        TimeoutException => "Timeout",
                        HttpRequestException => "HttpRequest",
                        InvalidDataException => "InvalidOfficialResponse",
                        InvalidOperationException when error.Message.StartsWith("来源冷却中",StringComparison.Ordinal) => "SourceCooldown",
                        _ when error.GetType().Name=="SourceBudgetUnavailableException" => "SourceBudgetUnavailable",
                        _ => error.GetType().Name
                    };
                    issues++;contiguous=false;await ReportAsync(new("SourceFailed",day,market));
                }
                await RunAsync(()=>{repository!.SaveMarketActivity(market,day,source);return true;},token);
                if(source is not null)
                {
                    if(source.Observation is not null)received++;
                    else
                    {
                        empty++;
                        if(trading==true){issues++;contiguous=false;firstFailure??="OpenDayNoOfficialData";}
                    }
                    await ReportAsync(new("SourceParsed",day,market,Received:source.Observation is null?0:1));
                }
            }
            if(track && contiguous)await RunAsync(()=>{repository!.RecordMarketActivityCursor(day);return true;},token);
            days++;await ReportAsync(new("DayCommitted",day));
        }
        await ReportAsync(new("RangeCompleted",to,Received:received));return new(days,issues,received,sourceCalls,closed,empty,firstFailure);
    }
}
