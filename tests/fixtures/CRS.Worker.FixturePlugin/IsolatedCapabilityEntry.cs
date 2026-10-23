using CRS.Worker.Abstractions;

namespace CRS.Worker.FixturePlugin;

/// <summary>隔离监督专用夹具：不联网，只通过宿主能力申请额度、提交进度和模拟通知。</summary>
public sealed class IsolatedCapabilityEntry:IContextualBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession()=>throw new InvalidOperationException();
    public IBackgroundPluginSession CreateSession(IBackgroundHostContext context)=>new CapabilitySession(context);
    private sealed class CapabilitySession(IBackgroundHostContext context):IBackgroundPluginSession,IParameterizedBackgroundSession,
        IProgressBackgroundSession,IBackgroundQuerySession
    {
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>[new("one","隔离能力夹具","0 0 0 1 1 ?","China Standard Time",false,false,true)];
        public ValueTask StartAsync(CancellationToken token)
        {var key=context.OpenDataKey();try{if(key.Length!=32)throw new InvalidDataException();}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);}return ValueTask.CompletedTask;}
        public Task<BackgroundJobResult> ExecuteAsync(string id,CancellationToken token)=>ExecuteAsync(id,null,token);
        public Task<BackgroundJobResult> ExecuteAsync(string id,string? payload,CancellationToken token,Guid? commandId=null)
            =>ExecuteWithProgressAsync(id,payload,_=>Task.CompletedTask,token,commandId);
        public async Task<BackgroundJobResult> ExecuteWithProgressAsync(string id,string? payload,Func<BackgroundProgress,Task> progress,CancellationToken token,Guid? commandId=null)
        {
            using var lease=await ((IBackgroundSourceContext)context).AcquireSourceAsync("www.sse.com.cn",new(DateTimeOffset.MinValue,DateTimeOffset.MinValue,0),token);
            await progress(new("RangeStarted"));
            // 故意忽略取消，证明父进程能只终止这个Runner并收回正在占用的来源额度。
            if(payload=="hang")await Task.Delay(Timeout.InfiniteTimeSpan);
            var notification=(IBackgroundNotificationContext)context;
            var channels=await notification.NotificationChannelsAsync(token);
            var sent=await notification.SendNotificationAsync(channels.Single().Id,"fixture",new("fixture-message","合成通知","无真实收件人","Fixture"),token);
            if(sent.State!="Accepted")throw new InvalidOperationException();
            lease.Complete(200);return BackgroundJobResult.Success("command="+commandId?.ToString("N"));
        }
        public Task<string> QueryAsync(string payload,CancellationToken token)
        {
            if(payload=="crash")Environment.Exit(17);
            if(payload=="hang")return Task.Delay(Timeout.InfiniteTimeSpan).ContinueWith(_=>"unreachable",TaskScheduler.Default);
            if(payload=="reject")throw new InvalidDataException("synthetic-private-message");
            return Task.FromResult(context.PluginId+":"+Environment.ProcessId);
        }
        public ValueTask StopAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
