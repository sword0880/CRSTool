using System.Collections.Concurrent;
using System.Text;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeam.Jobs;

/// <summary>用阻塞响应证明日期真实重叠、反压和取消；不访问交易所或已安装后台。</summary>
internal static class MarketActivityParallelChecks
{
    private static readonly DateOnly Day=new(2026,10,13);
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MarketActivitySource Value(string market,DateOnly day)=>new(OfficialMarketActivitySource.Request(market,day),
        Encoding.UTF8.GetBytes($"synthetic-parallel-{market}-{day}"),market,day,new(market,day,100,200));
    private static void Check(bool value,string name)
    {if(!value)throw new InvalidOperationException(name);Console.WriteLine("PASS 日期并行："+name);}
    public static async Task RunAsync(string root)
    {
        await IndependentDates(Path.Combine(root,"dates"));
        await BoundedCancellation(Path.Combine(root,"bounded"));
        await OrderedCursor(Path.Combine(root,"cursor"));
        await ProducerFailure(Path.Combine(root,"producer-failure"));
    }
    private static async Task IndependentDates(string root)
    {
        var firstSse=Signal();var releaseSse=Signal();var overlapping=Signal();var releaseDates=Signal();var szseCommitted=Signal();var entered=0;
        var progress=new ConcurrentQueue<EtfProgress>();var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        await using var workspace=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
        await workspace.InitializeAsync(default);
        workspace.SetProgressSink(p=>{progress.Enqueue(p);if(p is{Stage:"SourceParsed",Market:"SZSE"}&&p.Date==Day.AddDays(4))szseCommitted.TrySetResult();return Task.CompletedTask;});
        workspace.Activity.SetMarketActivitySource(new Source(async(market,day,token)=>
        {
            if(market=="SSE"&&day==Day){firstSse.TrySetResult();await releaseSse.Task.WaitAsync(token);}
            if(market=="SZSE"&&(day==Day.AddDays(1)||day==Day.AddDays(2)))
            {if(Interlocked.Increment(ref entered)==2)overlapping.TrySetResult();await releaseDates.Task.WaitAsync(token);}
            return Value(market,day);
        }));
        var running=workspace.Activity.SyncMarketActivityReportAsync(Day,Day.AddDays(4),default);
        try
        {
            await firstSse.Task.WaitAsync(TimeSpan.FromSeconds(5));await overlapping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!running.IsCompleted&&entered==2,"同一市场两个日期真实同时等待响应，不被另一市场首日阻塞");
            releaseDates.TrySetResult();await szseCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var partial=await workspace.Activity.MarketActivityAsync(default);
            Check(partial.Single(x=>x.Date==Day.AddDays(4)).AmountYuan is null,"快市场全部日期已落盘，缺另一市场仍不生成两市总额");
        }
        finally{releaseDates.TrySetResult();releaseSse.TrySetResult();}
        var result=await running.WaitAsync(TimeSpan.FromSeconds(10));
        Check(result is{QueriedDays:5,ReceivedRows:10,RequestedSources:10,SourceIssues:0},"五个日期十份市场结果各提交一次");
        var daily=progress.Where(p=>p.Stage=="MarketDayCompleted").ToArray();
        Check(daily.Length==5&&daily.All(p=>p.Received==2)&&daily.Sum(p=>p.Received)==result.ReceivedRows,"乱序响应的当日和区间进度计数一致");
    }
    private static async Task BoundedCancellation(string root)
    {
        var paused=Signal();using var stop=new CancellationTokenSource();var calls=0;var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        await using var workspace=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
        await workspace.InitializeAsync(default);
        workspace.SetProgressSink(async p=>{if(p.Stage=="SourceParsed"){paused.TrySetResult();await Task.Delay(Timeout.Infinite,stop.Token);}});
        workspace.Activity.SetMarketActivitySource(new Source((market,day,token)=>{Interlocked.Increment(ref calls);return Task.FromResult(Value(market,day));}));
        var running=workspace.Activity.SyncMarketActivityReportAsync(Day.AddDays(-200),Day,stop.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(200);
        Check(calls<=7,"写入消费者暂停时最多4个生产任务、2份排队和1份正在提交的结果");
        stop.Cancel();try{await running.WaitAsync(TimeSpan.FromSeconds(5));throw new InvalidOperationException("取消未生效。");}catch(OperationCanceledException){}
        var after=calls;await Task.Delay(100);Check(calls==after,"取消解除满队列等待，退出后不再产生迟到下载");
        workspace.SetProgressSink(null);workspace.Activity.SetMarketActivitySource(new Source((market,day,token)=>Task.FromResult(Value(market,day))));
        var retry=await workspace.Activity.SyncMarketActivityReportAsync(Day,Day,default);
        Check(retry is{ReceivedRows:2,SourceIssues:0},"取消没有损坏会话或后续采集");
    }
    private static async Task OrderedCursor(string root)
    {
        var from=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime).AddDays(-35);
        var release=Signal();var futureFailure=Signal();var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        await using var workspace=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
        await workspace.InitializeAsync(default);
        workspace.SetProgressSink(p=>{if(p is{Stage:"SourceFailed",Market:"SZSE"}&&p.Date==from.AddDays(1))futureFailure.TrySetResult();return Task.CompletedTask;});
        workspace.Activity.SetMarketActivitySource(new Source(async(market,day,token)=>
        {
            if(market=="SSE"&&day==from)await release.Task.WaitAsync(token);
            if(market=="SZSE"&&day==from.AddDays(1))throw new InvalidDataException("synthetic later date failure");
            return Value(market,day);
        }));
        var running=workspace.Activity.SyncRecentMarketActivityAsync(default);
        try
        {
            await futureFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var read=TestMarketData.Open(Path.Combine(root,"data"),raw);
            Check(read.ReadMarketActivityCursor() is null,"未来日期先结束时不跨过尚未完成的首日");
        }
        finally{release.TrySetResult();}
        var report=await running.WaitAsync(TimeSpan.FromSeconds(15));
        using var repository=TestMarketData.Open(Path.Combine(root,"data"),raw);
        Check(repository.ReadMarketActivityCursor()==from&&report.FirstFailureDate==from.AddDays(1),"先返回的后续失败不阻止有效前缀，也不会推进游标跨过缺口");
    }
    private static async Task ProducerFailure(string root)
    {
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"));var waiting=Signal();var cancelled=0;
        await using var workspace=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
        await workspace.InitializeAsync(default);
        workspace.SetProgressSink(async p=>
        {
            if(p.Stage=="DayStarted"&&p.Date==Day.AddDays(2))
            {await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));throw new InvalidOperationException("synthetic producer failure");}
        });
        workspace.Activity.SetMarketActivitySource(new Source(async(market,day,token)=>
        {
            if(day==Day)return Value(market,day);
            waiting.TrySetResult();
            try{await Task.Delay(Timeout.Infinite,token);return Value(market,day);}
            catch(OperationCanceledException){Interlocked.Increment(ref cancelled);throw;}
        }));
        try{await workspace.Activity.SyncMarketActivityReportAsync(Day,Day.AddDays(4),default).WaitAsync(TimeSpan.FromSeconds(5));throw new Exception("生产任务故障未上抛。");}
        catch(InvalidOperationException error) when(error.Message=="synthetic producer failure"){}
        Check(cancelled>0,"单个日期生产任务故障会立即取消并排空其它阻塞下载");
    }
    private sealed class Source(Func<string,DateOnly,CancellationToken,Task<MarketActivitySource>> fetch):IMarketActivitySource
    {public Task<MarketActivitySource> FetchAsync(string market,DateOnly day,CancellationToken token)=>fetch(market,day,token);}
}
