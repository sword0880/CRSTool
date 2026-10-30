using CRS.Common.Concurrency;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>后台用例共用的单写门槛、独立读快照和生命周期；不承载业务规则或界面端口。</summary>
internal sealed class MarketApplicationSession(Func<IMarketRepository> factory) : IAsyncDisposable
{
    private readonly BoundedWorkAdmission writeGate=new(1,4,TimeSpan.FromSeconds(5));
    private readonly BoundedWorkAdmission shortReads=new(2,4,TimeSpan.FromSeconds(1));
    private readonly BoundedWorkAdmission researchReads=new(1,1,TimeSpan.FromSeconds(2));
    private readonly BoundedWorkAdmission backgroundReads=new(1,4,TimeSpan.FromSeconds(2));
    private readonly BoundedWorkAdmission evaluationReads=new(1,1,TimeSpan.FromSeconds(2));
    private readonly BoundedWorkAdmission evaluations=new(1,1,TimeSpan.FromSeconds(2));
    private IMarketRepository? repository;
    private IMarketReadSessions? independentReads;
    private bool disposed;
    internal Func<EtfProgress,Task>? Progress {get;set;}
    internal Task ReportAsync(EtfProgress value)=>Progress?.Invoke(value)??Task.CompletedTask;
    internal async Task InitializeAsync(CancellationToken token)
    {
        using(await EnterAsync(writeGate,token).ConfigureAwait(false))
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
    }
    internal async Task<T> WriteAsync<TPort,T>(Func<TPort,T> work,CancellationToken token,Action<long>? heldMilliseconds=null) where TPort:class
    {
        using(await EnterAsync(writeGate,token).ConfigureAwait(false))
        {
            var holding=System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
            ObjectDisposedException.ThrowIf(disposed,this);
            var current=repository??throw new InvalidOperationException("后台用例会话尚未初始化。");
            var port=current as TPort??throw new NotSupportedException("市场仓储缺少请求的业务写入能力。");
            return await Task.Run(()=>current.ExecuteOperation(()=>work(port),token),token).ConfigureAwait(false);
            }
            finally{heldMilliseconds?.Invoke((long)System.Diagnostics.Stopwatch.GetElapsedTime(holding).TotalMilliseconds);}
        }
    }
    internal async Task<T> EvaluateAsync<T>(Func<Task<T>> work,CancellationToken token)
    {
        using(await EnterAsync(evaluations,token).ConfigureAwait(false))
        {ObjectDisposedException.ThrowIf(disposed,this);return await work().ConfigureAwait(false);}
    }
    internal Task<T> ReadAsync<TPort,T>(Func<TPort,T> query,CancellationToken token) where TPort:class
    {
        var admission=ReadWorkloadScope.Current switch{ReadWorkloadKind.ShortRead=>shortReads,ReadWorkloadKind.ResearchRead=>researchReads,_=>backgroundReads};
        return ReadWithAdmissionAsync(admission,query,token);
    }
    // 评价准备使用独立连接和名额；长时间核验不能占住采集的只读门槛，关闭仍统一排空。
    internal Task<IPreparedExitAlertEvaluation?> PrepareExitAlertEvaluationAsync(IExitAlertEvaluationVersion version,bool force,CancellationToken token)
        =>ReadWithAdmissionAsync<IExitAlertEvaluationRepository,IPreparedExitAlertEvaluation?>(evaluationReads,repository=>repository.PrepareExitAlertEvaluation(version,force),token);
    private async Task<T> ReadWithAdmissionAsync<TPort,T>(BoundedWorkAdmission admission,Func<TPort,T> query,CancellationToken token) where TPort:class
    {
        using(await EnterAsync(admission,token).ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var sessions=Volatile.Read(ref independentReads)??throw new InvalidOperationException("后台用例会话尚未初始化。");
            return await Task.Run(()=>sessions.Read(current=>query(current as TPort
                ??throw new NotSupportedException("市场仓储缺少请求的业务读取能力。")),token),token).ConfigureAwait(false);
        }
    }
    public async ValueTask DisposeAsync()
    {
        disposed=true;
        await Task.WhenAll(shortReads.CloseAsync(),researchReads.CloseAsync(),backgroundReads.CloseAsync(),evaluationReads.CloseAsync(),writeGate.CloseAsync(),evaluations.CloseAsync()).ConfigureAwait(false);
        Volatile.Write(ref independentReads,null);repository?.Dispose();repository=null;
    }
    private async ValueTask<IDisposable> EnterAsync(BoundedWorkAdmission admission,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        try{return await admission.EnterAsync(token).ConfigureAwait(false);}
        catch(WorkAdmissionException error) when(error.Reason=="Closed"){throw new ObjectDisposedException(nameof(MarketApplicationSession));}
    }
}
