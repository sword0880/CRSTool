// 按优先级管理有界读取名额；等待预算、取消与关闭必须释放等待者，优先级不能绕过容量上限。
namespace CRS.Common.Concurrency;

/// <summary>
/// Per-instance bounded async admission. Priority queries receive at most a configurable burst
/// before a queued background reader proceeds. No global state, tenant identifiers or file paths.
/// </summary>
public sealed class PriorityReadAdmission
{
    private readonly object sync=new();
    private readonly Queue<TaskCompletionSource<IDisposable>> queries=new(),background=new();
    private readonly int capacity,maximumQueryBurst;
    private int active,queryBurst;

    public PriorityReadAdmission(int capacity,int maximumQueryBurst=8)
    {
        if(capacity is <1 or >256)throw new ArgumentOutOfRangeException(nameof(capacity));
        if(maximumQueryBurst is <1 or >256)throw new ArgumentOutOfRangeException(nameof(maximumQueryBurst));
        this.capacity=capacity;this.maximumQueryBurst=maximumQueryBurst;
    }

    public PriorityReadAdmissionActivity Activity
    {
        get { lock(sync)return new(active,queries.Count(x=>!x.Task.IsCompleted),background.Count(x=>!x.Task.IsCompleted)); }
    }

    public async ValueTask<IDisposable> EnterAsync(bool priority,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<IDisposable> waiter;
        lock(sync)
        {
            if(active<capacity && !HasWaiting())
            { active++;return new Lease(this); }
            waiter=new(TaskCreationOptions.RunContinuationsAsynchronously);
            (priority?queries:background).Enqueue(waiter);
        }
        using var registration=cancellationToken.Register(()=>waiter.TrySetCanceled(cancellationToken));
        var lease=await waiter.Task.ConfigureAwait(false);
        // Grant/cancel race: a granted-but-cancelled caller must never leak a capacity slot.
        if(cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return lease;
    }
    private bool HasWaiting()=>queries.Any(x=>!x.Task.IsCompleted)||background.Any(x=>!x.Task.IsCompleted);
    private void Release()
    {
        lock(sync)
        {
            active--;
            while(queries.Count>0||background.Count>0)
            {
                var priority=queries.Count>0&&(queryBurst<maximumQueryBurst||background.Count==0);
                var waiter=(priority?queries:background).Dequeue();
                if(waiter.Task.IsCompleted)continue;
                if(!waiter.TrySetResult(new Lease(this)))continue;
                active++;
                queryBurst=priority?Math.Min(maximumQueryBurst,queryBurst+1):0;return;
            }
        }
    }
    private sealed class Lease(PriorityReadAdmission owner):IDisposable
    {
        private PriorityReadAdmission? current=owner;
        public void Dispose()=>Interlocked.Exchange(ref current,null)?.Release();
    }
}
/// <summary>Only counters; no secrets, original bytes or evidence paths.</summary>
public sealed record PriorityReadAdmissionActivity(int Active,int PriorityWaiting,int BackgroundWaiting);
