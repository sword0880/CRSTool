using CRS.Plugin.Abstractions;
using CRS.Plugin.Wpf;

namespace CRS.Plugins.XueqiuWatch.Wpf;

public sealed class XueqiuPluginEntry : ICrsPluginEntry
{
    public IPluginSession CreateSession(IPluginHostContext context) => new Session();

    private sealed class Session : IPluginSession, IPluginWpfSession
    {
        private readonly CancellationTokenSource lifetime = new();

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<PluginPageContribution> GetPages() =>
            [new("opinions", "雪球观点追踪", () => new WatchPage(new WorkerWatchClient(), lifetime.Token))];

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            lifetime.Cancel();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            lifetime.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
