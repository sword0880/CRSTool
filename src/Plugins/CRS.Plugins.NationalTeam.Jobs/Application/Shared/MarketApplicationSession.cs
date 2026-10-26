namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>后台用例共用的单写门槛、独立读快照和生命周期；不承载业务规则或界面端口。</summary>
internal sealed class MarketApplicationSession(Func<IMarketRepository> factory) : IAsyncDisposable
{
    private readonly SemaphoreSlim writeGate=new(1,1);
    private readonly SemaphoreSlim readGate=new(1,1);
    private IMarketRepository? repository;
    private IMarketReadSessions? independentReads;
    private bool disposed;
    internal Func<EtfProgress,Task>? Progress {get;set;}
    internal Task ReportAsync(EtfProgress value)=>Progress?.Invoke(value)??Task.CompletedTask;
    internal async Task InitializeAsync(CancellationToken token)
    {
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(repository is not null)throw new InvalidOperationException("后台用例会话已初始化。");
            await Task.Run(()=>
            {
                var opened=factory();
                try{MarketRepositoryCapabilities.Validate(opened,token);}
                catch{opened.Dispose();throw;}
                repository=opened;
                Volatile.Write(ref independentReads,(IMarketReadSessions)opened);
            },token).ConfigureAwait(false);
        }
        finally{writeGate.Release();}
    }
    internal async Task<T> WriteAsync<TPort,T>(Func<TPort,T> work,CancellationToken token) where TPort:class
    {
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var current=repository??throw new InvalidOperationException("后台用例会话尚未初始化。");
            var port=current as TPort??throw new NotSupportedException("市场仓储缺少请求的业务写入能力。");
            return await Task.Run(()=>current.ExecuteOperation(()=>work(port),token),token).ConfigureAwait(false);
        }
        finally{writeGate.Release();}
    }
    internal async Task<T> ReadAsync<TPort,T>(Func<TPort,T> query,CancellationToken token) where TPort:class
    {
        await readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var sessions=Volatile.Read(ref independentReads)??throw new InvalidOperationException("后台用例会话尚未初始化。");
            return await Task.Run(()=>sessions.Read(current=>query(current as TPort
                ??throw new NotSupportedException("市场仓储缺少请求的业务读取能力。")),token),token).ConfigureAwait(false);
        }
        finally{readGate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        await readGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await writeGate.WaitAsync().ConfigureAwait(false);
            try{disposed=true;Volatile.Write(ref independentReads,null);repository?.Dispose();repository=null;}
            finally{writeGate.Release();}
        }
        finally{readGate.Release();}
    }
}
