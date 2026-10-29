using CRS.Common.Concurrency;
using CRS.Worker.Abstractions;

namespace CRS.Worker.FixturePlugin;

/// <summary>只在测试自有目录阻塞真实准入，不联网、不调用通知或正式服务。</summary>
public sealed class AdmissionFailureEntry:IBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession()=>new Session();
    private sealed class Session:IBackgroundPluginSession,IParameterizedBackgroundSession,IBackgroundQuerySession,IConcurrentBackgroundSession
    {
        private readonly BoundedWorkAdmission admission=new(1,1,TimeSpan.FromMilliseconds(300));
        private int writes;
        public int MaximumConcurrentExecutions=>3;
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>[new("one","准入故障夹具","0 0 0 1 1 ?","China Standard Time",false,false,true)];
        public ValueTask StartAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public Task<BackgroundJobResult> ExecuteAsync(string id,CancellationToken token)=>ExecuteAsync(id,null,token);
        public async Task<BackgroundJobResult> ExecuteAsync(string id,string? payload,CancellationToken token,Guid? commandId=null)
        {
            if(payload=="io")throw new IOException("synthetic-private-url-and-key");
            if(payload=="inputs-changed")throw new WorkAdmissionException("InputsChanged");
            using var lease=await admission.EnterAsync(token);
            if(payload?.StartsWith("hold:",StringComparison.Ordinal)==true)
            {
                var marker=payload[5..];File.WriteAllText(marker,"holding");
                while(File.Exists(marker))await Task.Delay(10,token);
                return BackgroundJobResult.Success("holder-released");
            }
            Interlocked.Increment(ref writes);
            return BackgroundJobResult.Success("command="+commandId?.ToString("N"));
        }
        public Task<string> QueryAsync(string payload,CancellationToken token)
        {
            if(payload.StartsWith("admit:",StringComparison.Ordinal))throw new WorkAdmissionException(payload[6..]);
            if(payload=="io")throw new IOException("synthetic-private-url-and-key");
            if(payload=="timeout")throw new TimeoutException("synthetic-private-timeout");
            if(payload=="unknown")throw new ArithmeticException("synthetic-private-unknown");
            return Task.FromResult(payload=="writes"?Volatile.Read(ref writes).ToString():payload=="scope"?ReadWorkloadScope.Current.ToString():Environment.ProcessId.ToString());
        }
        public ValueTask StopAsync(CancellationToken token)=>new(admission.CloseAsync().WaitAsync(token));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
