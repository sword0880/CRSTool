namespace CRS.Worker.Ipc;

/// <summary>客户端按插件和任务保留待确认请求；未知结果不丢ID，不同任务独立，名额有界。</summary>
public sealed class BackgroundCommandTracker
{
    private sealed class Entry(BackgroundRequest request)
    {public BackgroundRequest Request{get;}=request;public bool Active;public bool Attempted;}
    private readonly object gate=new();
    private readonly Dictionary<(string Plugin,string Job),Entry> entries=[];
    private readonly int maximum;
    public BackgroundCommandTracker(int maximum=16)
    {if(maximum is <1 or >64)throw new ArgumentOutOfRangeException(nameof(maximum));this.maximum=maximum;}
    public BackgroundRequest? Find(string plugin,string job)
    {lock(gate)return entries.GetValueOrDefault((plugin,job))?.Request;}
    public Lease Acquire(string plugin,string job,string? payload)
    {
        if(string.IsNullOrWhiteSpace(plugin)||string.IsNullOrWhiteSpace(job))throw new ArgumentException("后台任务身份无效。");
        lock(gate)
        {
            var key=(plugin,job);
            if(entries.TryGetValue(key,out var entry))
            {
                if(entry.Request.Payload!=payload)throw new BackgroundCommandTrackingException("Conflict");
                if(entry.Active)throw new BackgroundCommandTrackingException("InFlight");
            }
            else
            {
                if(entries.Count>=maximum)throw new BackgroundCommandTrackingException("Capacity");
                entry=new(new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",plugin,job,Payload:payload));entries.Add(key,entry);
            }
            var first=!entry.Attempted;entry.Attempted=true;entry.Active=true;
            return new(entry.Request,first,()=>Release(entry));
        }
    }
    /// <summary>由调用方在可信终态或首次明确拒绝后释放；旧回执不能清空随后新建的请求。</summary>
    public void Forget(BackgroundRequest request)
    {
        if(request.PluginId is not{} plugin||request.JobId is not{} job)return;
        lock(gate)
            if(entries.TryGetValue((plugin,job),out var entry)&&entry.Request.RequestId==request.RequestId)entries.Remove((plugin,job));
    }
    private void Release(Entry entry){lock(gate)entry.Active=false;}
    public sealed class Lease:IDisposable
    {
        private Action? release;
        internal Lease(BackgroundRequest request,bool first,Action release){Request=request;FirstAttempt=first;this.release=release;}
        public BackgroundRequest Request{get;}
        public bool FirstAttempt{get;}
        public void Dispose()=>Interlocked.Exchange(ref release,null)?.Invoke();
    }
}
public sealed class BackgroundCommandTrackingException(string reason):InvalidOperationException("后台命令等待确认。")
{public string Reason{get;}=reason;}
