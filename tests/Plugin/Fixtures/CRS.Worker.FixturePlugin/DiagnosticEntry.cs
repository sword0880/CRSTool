using CRS.Worker.Abstractions;
namespace CRS.Worker.FixturePlugin;
public sealed class DiagnosticEntry:IBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession()=>new Session();
    private sealed class Session:IBackgroundPluginSession
    {
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>[new("one","诊断","0/1 * * * * ?","China Standard Time",false){Diagnostic=true}];
        public ValueTask StartAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public Task<BackgroundJobResult> ExecuteAsync(string job,CancellationToken token)=>Task.FromResult(BackgroundJobResult.Success("diagnostic=Passed"));
        public ValueTask StopAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
