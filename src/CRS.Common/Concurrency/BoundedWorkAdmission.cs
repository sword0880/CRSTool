namespace CRS.Common.Concurrency;

/// <summary>实例内有界FIFO准入；等待超时、取消和关闭都移除排队项，关闭后排空在途租约。</summary>
public sealed class BoundedWorkAdmission(int capacity,int maximumWaiting,TimeSpan waitBudget)
{
    private readonly object sync=new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> waiting=[];
    private readonly TaskCompletionSource drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active;
    private bool closed;
    private readonly int capacity=capacity is >=1 and <=256?capacity:throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly int maximumWaiting=maximumWaiting is >=0 and <=256?maximumWaiting:throw new ArgumentOutOfRangeException(nameof(maximumWaiting));
    private readonly TimeSpan waitBudget=waitBudget>=TimeSpan.Zero&&waitBudget<=TimeSpan.FromMinutes(2)?waitBudget:throw new ArgumentOutOfRangeException(nameof(waitBudget));
    public async ValueTask<IDisposable> EnterAsync(CancellationToken token,TimeSpan? budget=null)
    {
        if(budget is {} requested&&(requested<TimeSpan.Zero||requested>TimeSpan.FromMinutes(2)))throw new ArgumentOutOfRangeException(nameof(budget));
        token.ThrowIfCancellationRequested();
        LinkedListNode<TaskCompletionSource<IDisposable>> node;
        lock(sync)
        {
            if(closed)throw new WorkAdmissionException("Closed");
            if(active<capacity&&waiting.Count==0){active++;return new Lease(this);}
            if(waiting.Count>=maximumWaiting)throw new WorkAdmissionException("Capacity");
            node=waiting.AddLast(new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget??waitBudget);
        using var registration=deadline.Token.Register(()=>
        {
            lock(sync)
            {
                if(node.List is not null)waiting.Remove(node);
                if(token.IsCancellationRequested)node.Value.TrySetCanceled(token);
                else node.Value.TrySetException(new WorkAdmissionException("WaitBudget"));
            }
        });
        var lease=await node.Value.Task.ConfigureAwait(false);
        if(token.IsCancellationRequested){lease.Dispose();token.ThrowIfCancellationRequested();}
        return lease;
    }
    public Task CloseAsync()
    {
        lock(sync)
        {
            closed=true;
            foreach(var waiter in waiting)waiter.TrySetException(new WorkAdmissionException("Closed"));
            waiting.Clear();if(active==0)drained.TrySetResult();return drained.Task;
        }
    }
    private void Release()
    {
        lock(sync)
        {
            active--;
            while(!closed&&waiting.First is {} node)
            {
                waiting.RemoveFirst();if(!node.Value.TrySetResult(new Lease(this)))continue;
                active++;break;
            }
            if(closed&&active==0)drained.TrySetResult();
        }
    }
    private sealed class Lease(BoundedWorkAdmission owner):IDisposable
    {
        private BoundedWorkAdmission? current=owner;
        public void Dispose()=>Interlocked.Exchange(ref current,null)?.Release();
    }
}
public sealed class WorkAdmissionException(string reason):Exception("工作准入失败："+reason)
{
    public string Reason{get;}=reason;
}

/// <summary>沿异步调用传递只读成本类别，不携带用户、证券或可执行委托。</summary>
public static class ReadWorkloadScope
{
    private static readonly AsyncLocal<ReadWorkloadKind> current=new();
    public static ReadWorkloadKind Current=>current.Value;
    public static IDisposable Enter(bool isShort)
    {var previous=current.Value;current.Value=isShort?ReadWorkloadKind.ShortRead:ReadWorkloadKind.ResearchRead;return new Restore(previous);}
    private sealed class Restore(ReadWorkloadKind previous):IDisposable
    {
        private bool disposed;
        public void Dispose(){if(disposed)return;disposed=true;current.Value=previous;}
    }
}
public enum ReadWorkloadKind { Background,ShortRead,ResearchRead }
