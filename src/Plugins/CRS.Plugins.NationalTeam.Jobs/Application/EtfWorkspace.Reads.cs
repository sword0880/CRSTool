namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfWorkspace
{
    public Task<List<EvidenceRow>> EvidenceAsync(CRS.Plugins.NationalTeamWatch.Domain.EtfKey? key,DateOnly? from,DateOnly? to,CancellationToken token)
        =>ReadAsync(current=>current is IBoundedMarketEvidence bounded?bounded.Evidence(key,from,to,10000)
            :throw new NotSupportedException("存储提供方不支持有界证据查询。"),token);
    private readonly SemaphoreSlim readGate=new(1);
    private bool disposed;
    private async Task<T> ReadAsync<T>(Func<IMarketRepository,T> query,CancellationToken token)
    {
        await readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            IMarketReadSessions? sessions;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed,this);
                sessions=repository as IMarketReadSessions;
            }
            finally{gate.Release();}
            // 旧隔离夹具可沿用串行入口；正式SQLCipher读取不持写入门槛。
            return sessions is null?await RunAsync(()=>query(repository!),token).ConfigureAwait(false)
                :await Task.Run(()=>sessions.Read(query,token),token).ConfigureAwait(false);
        }
        finally{readGate.Release();}
    }
}
