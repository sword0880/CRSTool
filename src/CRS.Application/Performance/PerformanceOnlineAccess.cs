namespace CRS.Application.Performance;

/// <summary>会话内显式授权；撤销会取消已经开始的行情请求，默认不允许联网。</summary>
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
            if(authorization is null)throw new CrsException("当前为仅离线模式，请先选择允许在线更新并授权。");
            return CancellationTokenSource.CreateLinkedTokenSource(session,request,authorization.Token);
        }
    }
}
