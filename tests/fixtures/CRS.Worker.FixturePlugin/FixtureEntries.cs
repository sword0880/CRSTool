using CRS.Worker.Abstractions;
namespace CRS.Worker.FixturePlugin;

/// <summary>真实加载的多任务与生命周期故障夹具，所有副作用限定在测试显式给定的目录。</summary>
public sealed class GoodEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("Good"); }
public sealed class BadStartEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("BadStart"); }
public sealed class BadStopEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("BadStop"); }
public sealed class BadScheduleEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("BadSchedule"); }
public sealed class HangingStartEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("HangingStart"); }
public sealed class HangingJobEntry : IBackgroundPluginEntry { public IBackgroundPluginSession CreateSession() => new Session("HangingJob"); }
internal sealed class Session(string mode) : IBackgroundPluginSession
{
    private int concurrent;
    private int maximum;
    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs() =>
        [new("one", "夹具一", mode == "BadSchedule" ? "bad-cron" : "0 0 0 1 1 ?", "China Standard Time", false), new("two", "夹具二", "0 0 0 1 1 ?", "China Standard Time", false)];
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (mode == "BadStart") throw new InvalidOperationException("仅测试初始化故障");
        if (mode == "HangingStart") await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    public async Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref concurrent); maximum = Math.Max(maximum, current);
        try
        {
            var hold = Environment.GetEnvironmentVariable("CRS_WORKER_TEST_HOLD");
            if (jobId == "one" && hold is not null)
                while (File.Exists(hold)) await Task.Delay(25, mode == "HangingJob" ? CancellationToken.None : cancellationToken);
            await Task.Delay(150, cancellationToken);
            return BackgroundJobResult.Success("maximum=" + maximum);
        }
        finally { Interlocked.Decrement(ref concurrent); }
    }
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (mode == "BadStop") throw new InvalidOperationException("仅测试停止故障");
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
