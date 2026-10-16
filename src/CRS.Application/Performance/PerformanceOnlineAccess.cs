namespace CRS.Application.Performance;

/// <summary>会话内行情访问生命周期；关闭会话会取消已经开始的行情请求。</summary>
internal sealed class PerformanceOnlineAccess
{
    private readonly object gate=new();
    private CancellationTokenSource? authorization;
    internal bool Allowed {get{lock(gate)return authorization is not null;}}
    internal void SetAllowed(bool allowed)
    {
        CancellationTokenSource? previous;
        lock(gate)
        {
            if(allowed==(authorization is not null))return;
            previous=authorization;authorization=allowed?new():null;
        }
        previous?.Cancel();previous?.Dispose();
    }
    internal CancellationTokenSource Link(CancellationToken session,CancellationToken request)
    {
        lock(gate)
        {
            if(authorization is null)throw new CrsException("当前会话未启用在线行情服务。");
            return CancellationTokenSource.CreateLinkedTokenSource(session,request,authorization.Token);
        }
    }
}
