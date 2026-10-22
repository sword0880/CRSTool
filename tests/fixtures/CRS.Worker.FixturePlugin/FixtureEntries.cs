using CRS.Worker.Abstractions;
namespace CRS.Worker.FixturePlugin;

/// <summary>真实加载的多任务与生命周期故障夹具，所有副作用限定在测试显式给定的目录。</summary>
public sealed class QueryOnlyEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("QueryOnly"); }
public sealed class DiagnosticQueryEntry:IBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession()=>new DiagnosticSession();
    private sealed class DiagnosticSession:IBackgroundPluginSession,IBackgroundQuerySession,IBackgroundQueryDiagnosticsSession
    {
        public string QueryOperation(string payload)=>payload=="safe"?"market-activity":"https://private.example/?secret=fixture";
        public async Task<string> QueryAsync(string payload,CancellationToken token){await Task.Delay(Timeout.Infinite,token);return "unreachable";}
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>[];
        public ValueTask StartAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public Task<BackgroundJobResult> ExecuteAsync(string job,CancellationToken token)=>throw new NotSupportedException();
        public ValueTask StopAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
public sealed class GoodEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("Good"); }
// 故障消息仅含合成敏感标记，用于确认公开执行日志只持久化异常分类。
public sealed class BadJobEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("BadJob"); }
public sealed class BadStartEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("BadStart"); }
public sealed class BadStopEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("BadStop"); }
public sealed class BadScheduleEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("BadSchedule"); }
public sealed class HangingStartEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("HangingStart"); }
public sealed class HangingQueryEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("HangingQuery"); }
public sealed class HangingStopEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("HangingStop"); }
public sealed class HangingDisposeEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("HangingDispose"); }
public sealed class LateQueryEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("LateQuery"); }
public sealed class TimeoutOnCancelQueryEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("TimeoutOnCancelQuery"); }
public sealed class HangingJobEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("HangingJob"); }
public sealed class FloodEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("Flood"); }
public sealed class ParallelEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("Parallel"); }
public sealed class InvalidConcurrencyEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("InvalidConcurrency"); }
public sealed class DeferredEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("Deferred"); }
internal sealed class Session(string mode) : IBackgroundPluginSession,IProgressBackgroundSession,IBackgroundQuerySession,IConcurrentBackgroundSession
{
    public int MaximumConcurrentExecutions => mode == "Parallel" ? 2 : mode == "InvalidConcurrency" ? 33 : 1;
    public async Task<string> QueryAsync(string payload, CancellationToken token)
    {
        if(mode=="HangingQuery")await Task.Delay(Timeout.InfiniteTimeSpan);
        if(mode=="TimeoutOnCancelQuery")
        {
            try{await Task.Delay(Timeout.InfiniteTimeSpan,token);}catch(OperationCanceledException){}
            throw new TimeoutException("synthetic completed timeout");
        }
        if(mode=="LateQuery")
        {
            try{await Task.Delay(Timeout.InfiniteTimeSpan,token);}catch(OperationCanceledException){}
            await Task.Delay(100);return "late-success-must-be-rejected";
        }
        if (payload == "fail") throw new IOException("synthetic-secret-not-for-public-log");
        if (payload == "state") return System.Text.Json.JsonSerializer.Serialize(new { Concurrent = Volatile.Read(ref concurrent), Maximum = Volatile.Read(ref maximum) });
        if (payload.StartsWith("slow:") || payload.StartsWith("cancel:"))
        {
            // 独立文件让验收等待真实进入插件，避免把线程调度延迟误作持锁证据。
            File.WriteAllText(payload[(payload.IndexOf(':') + 1)..], "entered");
            await Task.Delay(payload.StartsWith("slow:") ? TimeSpan.FromSeconds(15) : Timeout.InfiniteTimeSpan, token);
        }
        return "fixture-query-ok";
    }
    private int concurrent;
    private int maximum;
    public async Task<BackgroundJobResult> ExecuteWithProgressAsync(string jobId,string? payload,Func<BackgroundProgress,Task> report,CancellationToken token,Guid? commandId=null)
    {
        await report(new("DayStarted",new(2026,10,14),"SSE"));
        if(mode=="Deferred")
        {
            if(payload!="synthetic-historical-range"||commandId is null)return new("Failed","originalPayload=Missing");
            var hold=Environment.GetEnvironmentVariable("CRS_WORKER_TEST_DEFER");
            return hold is not null&&File.Exists(hold)?new("RetryDeferred","originalPayload=Preserved",TimeSpan.FromSeconds(30)):BackgroundJobResult.Success("originalPayload=Preserved");
        }
        return await ExecuteAsync(jobId,token);
    }
    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs() => mode=="QueryOnly"?[]:mode=="Deferred"?[new("one","延后重试夹具","0 0 0 1 1 ?","China Standard Time",false,false,true)]:mode == "Flood"
        ? Enumerable.Range(1,12).Select(i => new BackgroundJobDescriptor(i == 1 ? "one" : i == 2 ? "two" : "job"+i, "积压夹具"+i, "0 0 0 1 1 ?", "China Standard Time", false)).ToArray()
        : [new("one", "夹具一", mode == "BadSchedule" ? "bad-cron" : "0 0 0 1 1 ?", "China Standard Time", false), new("two", "夹具二", "0 0 0 1 1 ?", "China Standard Time", false)];
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (mode == "BadStart") throw new InvalidOperationException("仅测试初始化故障");
        if (mode == "HangingStart") await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    public async Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref concurrent);
        int observed;
        do { observed = Volatile.Read(ref maximum); } while (current > observed && Interlocked.CompareExchange(ref maximum, current, observed) != observed);
        try
        {
            if(mode=="BadJob")throw new InvalidOperationException("synthetic-secret-not-for-public-log");
            var hold = Environment.GetEnvironmentVariable("CRS_WORKER_TEST_HOLD");
            if ((jobId == "one" || mode == "Parallel") && hold is not null)
                while (File.Exists(hold)) await Task.Delay(25, mode == "HangingJob" ? CancellationToken.None : cancellationToken);
            await Task.Delay(150, cancellationToken);
            return BackgroundJobResult.Success("maximum=" + maximum);
        }
        finally { Interlocked.Decrement(ref concurrent); }
    }
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if(mode=="HangingStop")return new(Task.Delay(Timeout.InfiniteTimeSpan));
        if (mode == "BadStop") throw new InvalidOperationException("仅测试停止故障");
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => mode=="HangingDispose"?new(Task.Delay(Timeout.InfiniteTimeSpan)):ValueTask.CompletedTask;
}
