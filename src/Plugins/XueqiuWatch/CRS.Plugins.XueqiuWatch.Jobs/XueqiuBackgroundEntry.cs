using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Abstractions;

namespace CRS.Plugins.XueqiuWatch.Jobs;

public sealed class XueqiuBackgroundEntry : IContextualBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession() => throw new InvalidOperationException("观点追踪需要独立存储上下文。");
    public IBackgroundPluginSession CreateSession(IBackgroundHostContext context) => new Session(context);

    private sealed class Session(IBackgroundHostContext context) : IBackgroundPluginSession, IBackgroundPrivateDataSession
    {
        private readonly SemaphoreSlim gate = new(1);
        private WatchStore? store;

        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs() => [];
        public Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken cancellationToken)
            => Task.FromResult(new BackgroundJobResult("Rejected"));

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
            gate.Dispose();
        }
    }
}
