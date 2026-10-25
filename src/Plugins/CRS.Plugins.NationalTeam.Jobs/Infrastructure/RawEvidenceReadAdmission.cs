namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>同一原件库的全局读取额度；查询最多连续取得八个等待名额，之后让一个后台读取前进，禁止饿死采集。</summary>
internal sealed class RawEvidenceReadAdmission(int capacity)
{
    private readonly object sync=new();
    private readonly Queue<TaskCompletionSource<IDisposable>> queries=new(),background=new();
    private int active,queryBurst;
    public RawEvidenceReadActivity Activity
    {get{lock(sync)return new(active,queries.Count(x=>!x.Task.IsCompleted),background.Count(x=>!x.Task.IsCompleted));}}
    public async ValueTask<IDisposable> EnterAsync(bool query,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();TaskCompletionSource<IDisposable> waiter;
        lock(sync)
        {
            if(active<capacity){active++;return new Lease(this);}
            waiter=new(TaskCreationOptions.RunContinuationsAsynchronously);(query?queries:background).Enqueue(waiter);
        }
        // 取消只撤销本次等待；与授予竞争时由持有者的using及后续令牌检查归还名额。
        using var registration=token.Register(()=>waiter.TrySetCanceled(token));
        return await waiter.Task.ConfigureAwait(false);
    }
    private void Release()
    {
        lock(sync)
        {
            active--;
            while(queries.Count>0||background.Count>0)
            {
                var query=queries.Count>0&&(queryBurst<8||background.Count==0);
                var waiter=(query?queries:background).Dequeue();var lease=new Lease(this);
                if(!waiter.TrySetResult(lease))continue;
                active++;queryBurst=query?Math.Min(8,queryBurst+1):0;return;
            }
        }
    }
    private sealed class Lease(RawEvidenceReadAdmission owner):IDisposable
    {
        private RawEvidenceReadAdmission? current=owner;
        public void Dispose()=>Interlocked.Exchange(ref current,null)?.Release();
    }
}

/// <summary>只读资源计数，不含文件名、哈希、金融值或凭据；取消等待不计为仍在排队。</summary>
public sealed record RawEvidenceReadActivity(int Active,int QueryWaiting,int BackgroundWaiting);
