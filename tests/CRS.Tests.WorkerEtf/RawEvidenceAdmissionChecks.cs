using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>真实完整哈希循环使用可控流验证共享额度、查询优先、后台前进及取消归还，不用计时成绩冒充正式性能。</summary>
internal static class RawEvidenceAdmissionChecks
{
    public static async Task RunAsync()
    {
        var values=Enumerable.Range(0,28).Select(i=>Encoding.UTF8.GetBytes("admission-"+i)).ToArray();
        var contents=values.ToDictionary(x=>Convert.ToHexString(SHA256.HashData(x)));
        var hashes=contents.Keys.ToArray();var cancelledHash=hashes[27];var opened=new ConcurrentQueue<string>();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var active=0;var maximum=0;var first=0;
        var raw=new RawEvidenceStore("unused-isolated-root",null,path=>
        {
            var hash=Path.GetFileNameWithoutExtension(path);opened.Enqueue(hash);var current=Interlocked.Increment(ref active);
            maximum=Math.Max(maximum,current);
            return new HeldStream(contents[hash],Interlocked.Exchange(ref first,1)==0?release.Task:null,()=>Interlocked.Decrement(ref active));
        },1);
        var background=Task.Run(()=>raw.VerifyMany(hashes.Take(11),default));
        if(!SpinWait.SpinUntil(()=>raw.VerificationActivity.Active==1,5000))throw new Exception("后台读取未占有额度。");
        using var stop=new CancellationTokenSource();
        var cancelled=Task.Run(()=>{using var scope=raw.PrioritizeQueryVerification();return raw.VerifyMany([cancelledHash],stop.Token);});
        if(!SpinWait.SpinUntil(()=>raw.VerificationActivity.QueryWaiting==1,5000))throw new Exception("查询等待未登记。");
        stop.Cancel();try{await cancelled.WaitAsync(TimeSpan.FromSeconds(1));throw new Exception("等待取消返回部分核验。");}catch(OperationCanceledException){}
        if(opened.Contains(cancelledHash))throw new Exception("已取消等待仍打开原件。");
        var queryHashes=hashes.Skip(15).Take(12).ToHashSet();
        var query=Task.Run(()=>{using var scope=raw.PrioritizeQueryVerification();return raw.VerifyMany(queryHashes,default);});
        var otherBackground=Task.Run(()=>raw.VerifyMany(hashes.Skip(11).Take(4),default));
        if(!SpinWait.SpinUntil(()=>raw.VerificationActivity is{QueryWaiting:1,BackgroundWaiting:1},5000))throw new Exception("并行批次未同时进入共享等待。");
        release.TrySetResult();
        var results=await Task.WhenAll(background,otherBackground,query).WaitAsync(TimeSpan.FromSeconds(5));
        var order=opened.ToArray();var lastQuery=Array.FindLastIndex(order,queryHashes.Contains);
        if(maximum!=1||results.Any(x=>x.Values.Any(ok=>!ok))||!queryHashes.Contains(order[1])||!order.Skip(1).Take(lastQuery-1).Any(x=>!queryHashes.Contains(x))
            ||raw.VerificationActivity!=new RawEvidenceReadActivity(0,0,0))throw new Exception("共享额度、查询优先或后台公平性/归还不成立。");
        Console.WriteLine("PASS 原件共享准入：三批次最多一个句柄、查询优先、查询结束前后台已前进、取消等待不打开原件且额度归零。");
        await DefaultCapacityAndCancellation(hashes,contents);
    }
    private static async Task DefaultCapacityAndCancellation(string[] hashes,Dictionary<string,byte[]> contents)
    {
        using var stop=new CancellationTokenSource();var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var opened=0;var closed=0;
        var raw=new RawEvidenceStore("unused-isolated-root",openRead:path=>
        {Interlocked.Increment(ref opened);return new HeldStream(contents[Path.GetFileNameWithoutExtension(path)],held.Task,()=>Interlocked.Increment(ref closed));});
        var background=Task.Run(()=>raw.VerifyMany(hashes,stop.Token));
        if(!SpinWait.SpinUntil(()=>Volatile.Read(ref opened)==8,5000))throw new Exception("默认八个读取未进入。");
        var query=Task.Run(()=>{using var scope=raw.PrioritizeQueryVerification();return raw.VerifyMany(hashes,stop.Token);});
        try
        {
            if(!SpinWait.SpinUntil(()=>raw.VerificationActivity.QueryWaiting==8,5000)||opened!=8||raw.VerificationActivity.Active!=8)
                throw new Exception("并行查询把全库句柄额度乘倍。");
        }
        finally{stop.Cancel();}
        foreach(var task in new[]{background,query})
            try{await task.WaitAsync(TimeSpan.FromSeconds(2));throw new Exception("取消返回部分完整性结果。");}catch(OperationCanceledException){}
        if(opened!=closed||raw.VerificationActivity!=new RawEvidenceReadActivity(0,0,0))throw new Exception("共享读取取消后句柄或名额泄漏。");
        Console.WriteLine("PASS 默认共享八句柄：并行巡检/查询不乘倍，取消等待与在途读取均归还额度且释放原件。");
    }
    private sealed class HeldStream(byte[] bytes,Task? release,Action close):MemoryStream(bytes)
    {
        private int closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {if(release is not null)await release.WaitAsync(token);await Task.Delay(2,token);return await base.ReadAsync(buffer,token);}
        protected override void Dispose(bool disposing){if(disposing&&Interlocked.Exchange(ref closed,1)==0)close();base.Dispose(disposing);}
    }
}
