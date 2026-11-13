using CRS.Worker.Abstractions;

namespace CRS.Worker.FixturePlugin;

/// <summary>不联网的正式宿主能力夹具；写操作只经过持久任务，查询不改变来源状态。</summary>
public sealed class SourceBudgetEntry:IContextualBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession()=>throw new InvalidOperationException("Test host context required.");
    public IBackgroundPluginSession CreateSession(IBackgroundHostContext context)=>new SourceBudgetSession(context);
}
internal sealed class SourceBudgetSession(IBackgroundHostContext context):IBackgroundPluginSession
{
    private IBackgroundSourceContext Sources=>context as IBackgroundSourceContext??throw new InvalidOperationException("Shared source capability missing.");
    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>new[]{"reject-sse","request-sse","request-szse"}.Select(id=>
        new BackgroundJobDescriptor(id,"共享预算隔离夹具","0 0 0 1 1 ?","China Standard Time",false,false,true)).ToArray();
    public ValueTask StartAsync(CancellationToken token)=>ValueTask.CompletedTask;
    public async Task<BackgroundJobResult> ExecuteAsync(string jobId,CancellationToken token)
    {
        if(jobId is not ("reject-sse" or "request-sse" or "request-szse"))throw new ArgumentException("Unknown fixture job.");
        try
        {
            using var lease=await Sources.AcquireSourceAsync(jobId=="request-szse"?"www.szse.cn":"static.sse.com.cn",
                new(DateTimeOffset.MinValue,DateTimeOffset.MinValue,0),token);
            lease.Complete(jobId=="reject-sse"?429:200,jobId=="reject-sse"?DateTimeOffset.UtcNow.AddDays(2):null);
            return BackgroundJobResult.Success(jobId=="reject-sse"?"simulated=429;network=0":"lease=Granted;network=0");
        }
        catch(SourceBudgetUnavailableException error){return new("Rejected","source="+error.Code+";network=0");}
    }
    public ValueTask StopAsync(CancellationToken token)=>ValueTask.CompletedTask;
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
