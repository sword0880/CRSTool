using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Abstractions;
using CRS.Feed;

namespace CRS.Plugins.XueqiuWatch.Jobs;

public sealed partial class XueqiuBackgroundEntry : IContextualBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession() => throw new InvalidOperationException("观点追踪需要独立存储上下文。");
    public IBackgroundPluginSession CreateSession(IBackgroundHostContext context) =>
        new Session(context, new FeedClient(options: new() { UseSystemProxy = false }));
    internal IBackgroundPluginSession CreateSession(IBackgroundHostContext context, HttpMessageHandler transport) =>
        new Session(context, new FeedClient(transport));

    private sealed partial class Session(IBackgroundHostContext context, FeedClient feed) : IBackgroundPluginSession, IBackgroundPrivateDataSession, IProxyBackgroundSession
    {
        private readonly SemaphoreSlim gate = new(1);
        private WatchStore? store;

        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs() =>
            [new(WatchProtocol.LeadJobId, "发现雪球发言线索", "0 15 9 * * ?", "China Standard Time", false)
                { RequiresPlanCreation = true, SupportsVpn = true }];

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = context.OpenDataKey();
            try { store = new WatchStore(context.DataDirectory, key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            return ValueTask.CompletedTask;
        }

        public async Task<string> AccessPrivateDataAsync(string payload, CancellationToken token)
        {
            if (payload.Length > WatchProtocol.MaximumPayload)
                throw new InvalidDataException("观点追踪请求超出大小上限。");
            await gate.WaitAsync(token);
            try
            {
                var request = WatchProtocol.Decode<WatchRequest>(payload);
                var current = store ?? throw new InvalidOperationException("观点追踪尚未启动。");
                return WatchProtocol.Encode(current.Access(request, payload, token));
            }
            catch (ArgumentException error) { return WatchProtocol.Encode(new WatchReply(Error: error.Message)); }
            catch (JsonException) { return WatchProtocol.Encode(new WatchReply(Error: "记录格式无效。")); }
            finally { gate.Release(); }
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try { store?.Dispose(); store = null; }
            finally { gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(CancellationToken.None);
            feed.Dispose();
            gate.Dispose();
        }
    }
}
