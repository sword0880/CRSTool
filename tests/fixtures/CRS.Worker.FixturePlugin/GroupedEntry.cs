using CRS.Worker.Abstractions;
namespace CRS.Worker.FixturePlugin;

public sealed class GroupedEntry:IBackgroundPluginEntry
{public IBackgroundPluginSession CreateSession()=>new GroupedSession(false);}
public sealed class InvalidGroupEntry:IBackgroundPluginEntry
{public IBackgroundPluginSession CreateSession()=>new GroupedSession(true);}
internal sealed class GroupedSession(bool invalid):IBackgroundPluginSession,IGroupedBackgroundSession
{
    public int MaximumConcurrentExecutions=>4;
    public BackgroundExecutionGroup ExecutionGroup(string jobId)=>jobId.StartsWith("collect",StringComparison.Ordinal)
        ?new("collection",invalid&&jobId=="collect2"?3:2):new(jobId,1);
    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>new[]{"collect1","collect2","collect3","evaluation","management"}
        .Select(id=>new BackgroundJobDescriptor(id,id,"0 0 0 1 1 ?","China Standard Time",false)).ToArray();
    public ValueTask StartAsync(CancellationToken token)=>ValueTask.CompletedTask;
    public async Task<BackgroundJobResult> ExecuteAsync(string id,CancellationToken token)
    {
        var hold=Environment.GetEnvironmentVariable("CRS_WORKER_TEST_HOLD");
        while(id.StartsWith("collect",StringComparison.Ordinal)&&hold is not null&&File.Exists(hold))await Task.Delay(20,token);
        return BackgroundJobResult.Success("group="+ExecutionGroup(id).Id);
    }
    public ValueTask StopAsync(CancellationToken token)=>ValueTask.CompletedTask;
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
