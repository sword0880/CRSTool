using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class MarketActivityUseCases
{
    private sealed record ActivityDownload(string Market,DateOnly Day,bool? Trading,string State,MarketActivitySource? Source=null,Exception? Error=null);

    /// <summary>按市场、日期有界下载；慢日期不阻塞另一市场，入库仍由单消费者串行执行。</summary>
    private async IAsyncEnumerable<ActivityDownload> DownloadActivityDaysAsync(DateOnly from,DateOnly to,
        HashSet<(string Market,DateOnly Date)> existing,[EnumeratorCancellation] CancellationToken token)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        // 最多4个日期/市场下载、2份待提交结果，避免整段历史响应堆入内存。
        var channel=Channel.CreateBounded<ActivityDownload>(new BoundedChannelOptions(2){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
        var calendars=new ConcurrentDictionary<DateOnly,Lazy<Task<Dictionary<string,bool?>>>>();
        async Task<Dictionary<string,bool?>> Calendar(DateOnly day)
        {
            await ReportAsync(new("DayStarted",day));
            return await ReadPortAsync((IShareCollectionRepository r)=>new[]{"SSE","SZSE"}.ToDictionary(m=>m,m=>r.IsReviewedTradingDay(m,day)),stop.Token).ConfigureAwait(false);
        }
        async Task Market(string market)
        {
            var blocked=0;var next=from.DayNumber;
            async Task Download(DateOnly day)
            {
                stop.Token.ThrowIfCancellationRequested();
                var trading=(await calendars.GetOrAdd(day,d=>new(()=>Calendar(d))).Value.ConfigureAwait(false))[market];
                var state=trading==false?"Closed":existing.Contains((market,day))?"Reused":Volatile.Read(ref blocked)!=0?"Deferred":"Fetched";
                MarketActivitySource? source=null;Exception? failure=null;
                if(state=="Fetched")
                {
                    await ReportAsync(new("SourceStarted",day,market));
                    try{source=await activitySource!.FetchAsync(market,day,stop.Token).ConfigureAwait(false);}
                    catch(OperationCanceledException) when(stop.IsCancellationRequested){throw;}
                    catch(Exception error)
                    {
                        failure=error;var (code,until)=ActivityFailure(error);
                        if(BlocksActivitySource(code,until))Interlocked.Exchange(ref blocked,1);
                    }
                }
                await channel.Writer.WriteAsync(new(market,day,trading,state,source,failure),stop.Token).ConfigureAwait(false);
            }
            async Task Worker()
            {
                try
                {
                    while(true)
                    {
                        var number=Interlocked.Increment(ref next);if(number>to.DayNumber)break;
                        await Download(DateOnly.FromDayNumber(number)).ConfigureAwait(false);
                    }
                }
                catch{await stop.CancelAsync().ConfigureAwait(false);throw;}
            }
            // 首次响应先核对来源状态；已知冷却时不并行排入同源的整段日期。
            await Download(from).ConfigureAwait(false);
            await Task.WhenAll(Worker(),Worker()).ConfigureAwait(false);
        }
        async Task Complete()
        {
            try{await Task.WhenAll(GuardedMarket("SSE"),GuardedMarket("SZSE")).ConfigureAwait(false);channel.Writer.TryComplete();}
            catch(Exception error){channel.Writer.TryComplete(error);}
        }
        async Task GuardedMarket(string market)
        {
            try{await Market(market).ConfigureAwait(false);}
            catch{await stop.CancelAsync().ConfigureAwait(false);throw;}
        }
        var completion=Complete();
        try
        {
            await foreach(var value in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))yield return value;
        }
        finally
        {
            // 消费端存储失败、取消或退出时，撤销下载及满队列等待，排空后才释放会话。
            await stop.CancelAsync().ConfigureAwait(false);await completion.ConfigureAwait(false);
        }
    }
    private static (string Code,DateTimeOffset? Until) ActivityFailure(Exception error)
    {
        var code=error switch
        {
            SourceBudgetUnavailableException cooling when cooling.Code=="SourceCooldown"=>"SourceCooldown",
            TimeoutException=>"Timeout",HttpRequestException=>"HttpRequest",InvalidDataException=>"InvalidOfficialResponse",
            InvalidOperationException when error.Message.StartsWith("来源冷却中",StringComparison.Ordinal)=>"SourceCooldown",
            _ when error.GetType().Name=="SourceBudgetUnavailableException"=>"SourceBudgetUnavailable",
            _=>error.GetType().Name
        };
        return(code,error is SourceBudgetUnavailableException budget?budget.Until:error.Data["SourceRetryAt"] as DateTimeOffset?);
    }
    private static bool BlocksActivitySource(string code,DateTimeOffset? until)=>until is not null||code is "SourceCooldown" or "SourceBudgetUnavailable";
    private sealed class CollectionDay
    {
        public int Completed{get;private set;}
        public int Received,Reused,Closed,Empty,Deferred,Issues;
        public void Add(int received,int reused,int closed,int empty,int deferred,int issues)
        {Completed++;Received+=received;Reused+=reused;Closed+=closed;Empty+=empty;Deferred+=deferred;Issues+=issues;}
        public EtfProgress Progress(DateOnly day)=>new("MarketDayCompleted",day,Received:Received)
            {Reused=Reused,Closed=Closed,Empty=Empty,Deferred=Deferred,Issues=Issues};
    }
}
