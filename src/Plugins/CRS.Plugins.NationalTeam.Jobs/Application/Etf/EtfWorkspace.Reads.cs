namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfWorkspace
{
    public Task<List<EvidenceRow>> EvidenceAsync(CRS.Plugins.NationalTeamWatch.Domain.EtfKey? key,DateOnly? from,DateOnly? to,CancellationToken token)
        =>ReadAsync(current=>current is IBoundedMarketEvidence bounded?bounded.Evidence(key,from,to,10000)
            :throw new NotSupportedException("存储提供方不支持有界证据查询。"),token);
    private readonly SemaphoreSlim readGate=new(1);
    // 初始化完成后发布独立读取端口；生命周期由readGate保护，读取不能为取得端口排到长写用例之后。
    private IMarketReadSessions? independentReads;
    private bool disposed;
    // 业务模块只接收其读取端口；组合与独立会话仍由此处统一管理。
    private Task<T> ReadPortAsync<TPort,T>(Func<TPort,T> query,CancellationToken token) where TPort:class
        =>ReadAsync(current=>query((current as TPort)??throw new NotSupportedException("市场仓储缺少请求的业务读取能力。")),token);
    private async Task<T> ReadAsync<T>(Func<IMarketRepository,T> query,CancellationToken token)
    {
        await readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var sessions=Volatile.Read(ref independentReads);
            // 旧隔离夹具可沿用串行入口；正式SQLCipher读取不持写入门槛。
            return sessions is null?await RunAsync(()=>query(repository!),token).ConfigureAwait(false)
                :await Task.Run(()=>sessions.Read(query,token),token).ConfigureAwait(false);
        }
        finally{readGate.Release();}
    }
}
