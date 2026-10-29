namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>功能用例仅经类型化端口访问共享执行会话，不拥有连接或界面能力。</summary>
public abstract class MarketUseCaseScope
{
    private readonly MarketApplicationSession session;
    private protected MarketUseCaseScope(MarketApplicationSession session)=>this.session=session;
    protected Task<T> RunAsync<TPort,T>(Func<TPort,T> work,CancellationToken token) where TPort:class=>session.WriteAsync(work,token);
    protected Task<T> RunMeasuredAsync<TPort,T>(Func<TPort,T> work,CancellationToken token,Action<long> heldMilliseconds) where TPort:class=>session.WriteAsync(work,token,heldMilliseconds);
    protected Task<T> ReadPortAsync<TPort,T>(Func<TPort,T> query,CancellationToken token) where TPort:class=>session.ReadAsync(query,token);
    protected Task ReportAsync(EtfProgress value)=>session.ReportAsync(value);
}
