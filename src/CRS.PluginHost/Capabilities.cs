using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using CRS.Plugin.Abstractions;

namespace CRS.PluginHost;
/// <summary>任务名在单个插件上下文内唯一，退出时通过关联令牌取消。</summary>
internal sealed class PluginJobs(CancellationToken lifetime) : IPluginJobScheduler
{
    private readonly ConcurrentDictionary<string, byte> running = new();
    public async Task RunAsync(string jobId, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId) || !running.TryAdd(jobId, 0)) throw new InvalidOperationException("Job already running.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        try { linked.Token.ThrowIfCancellationRequested(); await work(linked.Token).ConfigureAwait(false); }
        finally { running.TryRemove(jobId, out _); }
    }
}
/// <summary>公开来源限域、限速和大小保护；不跟随重定向，不绕过403或429。</summary>
internal sealed class PublicHttpGateway : IPluginHttpGateway, IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim gate = new(1);
    private DateTimeOffset next;
    private bool blocked;
    public async Task<PublicHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Host is not ("www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "www.szse.cn")) throw new InvalidOperationException("Public source not allowed.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (blocked) throw new InvalidOperationException("Source access blocked; restart after review.");
            var wait = next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            next = DateTimeOffset.UtcNow.AddSeconds(3);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 403 or 429) blocked = true;
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("Response too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
            { if (buffer.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("Response too large."); await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken).ConfigureAwait(false); }
            return new(status, buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        finally { gate.Release(); }
    }
    public void Dispose() { client.Dispose(); gate.Dispose(); }
}
internal sealed class Diagnostics(Action<string, string> report) : IPluginDiagnostics { public void Report(string code, string message) => report(code, message); }
/// <summary>未在清单中授予的能力在访问时拒绝，避免无意扩大插件接口权限。</summary>
internal sealed class PluginContext(string pluginId, IPluginJobScheduler jobs, IPluginDataPaths paths,
    IPluginHttpGateway http, IPluginDiagnostics diagnostics, IReadOnlyCollection<string> granted) : IPluginHostContext
{
    public string PluginId=>pluginId;
    private T Require<T>(string capability,T value)=>granted.Contains(capability)?value:throw new InvalidOperationException("Capability not granted: "+capability);
    public IPluginJobScheduler Jobs=>Require("jobs",jobs);
    public IPluginDataPaths DataPaths=>Require("storage-private",paths);
    public IPluginHttpGateway Http=>Require("http-public",http);
    public IPluginDiagnostics Diagnostics=>Require("logging",diagnostics);
}
