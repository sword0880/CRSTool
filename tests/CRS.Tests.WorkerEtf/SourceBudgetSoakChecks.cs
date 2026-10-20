using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.Service;

/// <summary>真实时间、真实加密库的有限持续负载；仅取得租约，不访问网络或已安装服务。</summary>
internal static class SourceBudgetSoakChecks
{
    private static readonly string[] Hosts=["query.sse.com.cn","www.szse.cn","www.chinacnd.com"];
    private static readonly string[] Names=["SSE","SZSE","ISSUER-600153"];
    private static readonly SourceBudgetState Empty=new(DateTimeOffset.MinValue,DateTimeOffset.MinValue,0);
    public static async Task RunAsync(int seconds)
    {
        if(seconds is <60 or >600)throw new ArgumentOutOfRangeException(nameof(seconds),"持续负载须为60～600秒。");
        var root=Path.Combine(Path.GetTempPath(),"CRS.SourceBudgetSoak",Guid.NewGuid().ToString("N"));
        var key=RandomNumberGenerator.GetBytes(32);
        try{await ExecuteAsync(root,key,seconds);}
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    private static async Task ExecuteAsync(string root,byte[] key,int seconds)
    {
        var starts=Enumerable.Range(0,3).Select(_=>new ConcurrentQueue<DateTimeOffset>()).ToArray();
        var perPlugin=new ConcurrentDictionary<string,int>();
        var full=0;var cooled=0;var peakWaiting=0;var peakActive=0;var samples=0;
        var blockedAt=new DateTimeOffset?[2];
        var watch=Stopwatch.StartNew();
        var checks=0;
        void Check(bool value,string name)
        {if(!value)throw new InvalidOperationException(name);checks++;Console.WriteLine("PASS 持续负载："+name);}
        IReadOnlyList<SourceBudgetHealth> final;
        using(var sources=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key)))
        using(var cancel=new CancellationTokenSource())
        {
            var contexts=Enumerable.Range(0,8).Select(i=>new WorkerPluginContext("plugin-"+i,root,key,sources:sources)).ToArray();
            async Task Consumer(int index)
            {
                var context=contexts[index%contexts.Length];var market=index%3;
                while(!cancel.IsCancellationRequested)
                {
                    try
                    {
                        using var lease=await context.AcquireSourceAsync(Hosts[market],Empty,cancel.Token);
                        var state=sources.Snapshot().Single(x=>x.Source==Names[market]);
                        starts[market].Enqueue(state.NextUtc);
                        perPlugin.AddOrUpdate(context.PluginId,1,(_,value)=>value+1);
                        await Task.Delay(20,cancel.Token); // 模拟占用租约的请求，绝不调用HTTP。
                        lease.Complete(200);
                    }
                    catch(SourceBudgetUnavailableException error) when(error.Code is "SourceQueueFull" or "SourceCooldown")
                    {
                        if(error.Code=="SourceQueueFull")Interlocked.Increment(ref full);
                        else Interlocked.Increment(ref cooled);
                        try{await Task.Delay(50,cancel.Token);}catch(OperationCanceledException) when(cancel.IsCancellationRequested){return;}
                    }
                    catch(OperationCanceledException) when(cancel.IsCancellationRequested){return;}
                }
            }
            // 固定120个消费者，不为每次重试创建新任务。每个来源40个消费者，持续超出容量。
            var consumers=Enumerable.Range(0,120).Select(Consumer).ToArray();
            var blockedSse=false;var blockedSzse=false;
            try
            {
                while(watch.Elapsed<TimeSpan.FromSeconds(seconds))
                {
                    if(consumers.Any(x=>x.IsFaulted))throw new InvalidOperationException("持续负载消费者异常。");
                    if(!blockedSse&&watch.Elapsed.TotalSeconds>=seconds/3d)
                    {sources.ReportFailure(Hosts[0],429,DateTimeOffset.UtcNow.AddDays(2));blockedAt[0]=DateTimeOffset.UtcNow;blockedSse=true;}
                    if(!blockedSzse&&watch.Elapsed.TotalSeconds>=seconds*2d/3)
                    {sources.ReportFailure(Hosts[1],503);blockedAt[1]=DateTimeOffset.UtcNow;blockedSzse=true;}
                    var snapshot=sources.Snapshot();samples++;
                    if(snapshot.Any(x=>x.Active>1||x.Waiting>32))throw new InvalidOperationException("来源并发或等待突破上限。");
                    peakWaiting=Math.Max(peakWaiting,snapshot.Max(x=>x.Waiting));
                    peakActive=Math.Max(peakActive,snapshot.Max(x=>x.Active));
                    await Task.Delay(100);
                }
            }
            finally
            {
                cancel.Cancel();
                await Task.WhenAll(consumers).WaitAsync(TimeSpan.FromSeconds(5));
            }
            final=sources.Snapshot();
            Check(watch.Elapsed.TotalSeconds>=seconds,"完成真实墙钟持续负载窗口");
            Check(samples>=seconds*4&&peakWaiting==32&&peakActive==1,"持续采样验证单请求并发和32项队列边界");
            Check(full>0&&cooled>0,"持续超载与冷却拒绝均实际发生");
            Check(final.All(x=>x.Waiting==0&&x.Active==0),"停止后五秒内全部排空，无遗留租约");
            Check(perPlugin.Count==8&&perPlugin.Values.All(x=>x>0),"八个插件均有进展，无插件被永久饿死");
            foreach(var market in Enumerable.Range(0,3))
            {
                var times=starts[market].ToArray();
                Check(times.Length>=5,Names[market]+"实际获得多个串行租约");
                Check(times.Zip(times.Skip(1),(a,b)=>b-a).All(x=>x>=TimeSpan.FromSeconds(3)),Names[market]+"持续负载下仍保留三秒间隔");
            }
            Check(final.Single(x=>x.Source=="SSE").State=="Cooldown"&&final.Single(x=>x.Source=="SZSE").State=="Cooldown","429与503冷却保留到测试结束");
            Check(starts[0].All(x=>x.AddSeconds(-3)<=blockedAt[0])&&starts[1].All(x=>x.AddSeconds(-3)<=blockedAt[1]),"来源拒绝后未放行新租约");
            Check(starts[2].Any(x=>x.AddSeconds(-3)>blockedAt[1]),"两个来源冷却后第三来源仍继续推进");
        }
        using(var reopened=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key)))
        {
            foreach(var market in new[]{0,1})
            {
                var state=reopened.Snapshot().Single(x=>x.Source==Names[market]);
                Check(state.BlockedUtc==final.Single(x=>x.Source==Names[market]).BlockedUtc,"持续负载后重启保留"+Names[market]+"冷却");
                try{using var ignored=await reopened.AcquireAsync("after-restart",Hosts[market],Empty,default);throw new Exception("重启绕过来源拒绝。");}
                catch(SourceBudgetUnavailableException error){Check(error.Code=="SourceCooldown","新插件重启后仍拒绝"+Names[market]+"请求");}
            }
            using var healthy=await reopened.AcquireAsync("after-restart",Hosts[2],Empty,default);
            healthy.Complete(200);
            Check(true,"未故障来源在重启后仍可独立推进");
        }
        Directory.CreateDirectory("artifacts");
        var evidence=Path.GetFullPath($"artifacts/source-budgets-soak-{seconds}s.json");
        File.WriteAllText(evidence,JsonSerializer.Serialize(new
        {
            ObservedUtc=DateTimeOffset.UtcNow,DurationSeconds=watch.Elapsed.TotalSeconds,RequestedSeconds=seconds,
            Consumers=120,Plugins=8,NetworkRequests=0,Checks=checks,Samples=samples,
            QueueFullRejections=full,CooldownRejections=cooled,PeakWaiting=peakWaiting,PeakActive=peakActive,
            StartedBySource=Names.Select((name,i)=>new{Source=name,Count=starts[i].Count,ReservedNextUtc=starts[i].ToArray()}).ToArray(),
            StartedByPlugin=perPlugin.OrderBy(x=>x.Key).ToArray(),Final=final,TestRoot=root
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"来源预算持续负载通过：{checks}项。证据：{evidence}");
    }
}
