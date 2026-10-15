namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfWorkspace
{
    private IMarketActivitySource? activitySource;
    public void SetMarketActivitySource(IMarketActivitySource source)=>activitySource=source;
    public Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token)=>RunAsync(()=>repository!.MarketActivity(),token);
    public async Task<SyncReport> SyncRecentMarketActivityAsync(CancellationToken token)
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
    private async Task<SyncReport> SyncMarketActivityRangeAsync(DateOnly from,DateOnly to,CancellationToken token,bool track)
    {
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        if(from>to || to>today || to.DayNumber-from.DayNumber>400)throw new InvalidDataException("采集区间必须不超过400个自然日，不能包含未来日期。");
        if(activitySource is null)throw new InvalidOperationException("成交统计来源未注册。");
        int issues=0,received=0,days=0;var contiguous=true;
        for(var day=from;day<=to;day=day.AddDays(1))
        {
            token.ThrowIfCancellationRequested();await ReportAsync(new("DayStarted",day));
            foreach(var market in new[]{"SSE","SZSE"})
            {
                await ReportAsync(new("SourceStarted",day,market));MarketActivitySource? source=null;
                try {source=await activitySource.FetchAsync(market,day,token);}
                catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                catch(Exception){issues++;contiguous=false;await ReportAsync(new("SourceFailed",day,market));}
                await RunAsync(()=>{repository!.SaveMarketActivity(market,day,source);return true;},token);
                if(source is not null){if(source.Observation is not null)received++;await ReportAsync(new("SourceParsed",day,market,Received:source.Observation is null?0:1));}
            }
            if(track && contiguous)await RunAsync(()=>{repository!.RecordMarketActivityCursor(day);return true;},token);
            days++;await ReportAsync(new("DayCommitted",day));
        }
        await ReportAsync(new("RangeCompleted",to,Received:received));return new(days,issues,received);
    }
}
