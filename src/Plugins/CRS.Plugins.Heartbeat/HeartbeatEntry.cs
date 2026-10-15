using CRS.Worker.Abstractions;
namespace CRS.Plugins.Heartbeat;

/// <summary>先用无网络、无桌面的真实插件验证服务纵向链路。</summary>
public sealed class HeartbeatEntry : IBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession() => new Session();
    private sealed class Session : IBackgroundPluginSession
    {
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs() =>
            [new("heartbeat", "后台服务心跳", "0 * * * * ?", "China Standard Time", true, RunOnStartup: true)];
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (jobId != "heartbeat") throw new InvalidOperationException("未知心跳任务。");
            return Task.FromResult(BackgroundJobResult.Success(DateTimeOffset.UtcNow.ToString("O")));
        }
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
