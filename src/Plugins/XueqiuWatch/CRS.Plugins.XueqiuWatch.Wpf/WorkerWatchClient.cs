using System.IO;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Ipc;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class WorkerWatchClient
{
    private readonly BackgroundPipeClient pipe = new();
    private WatchRequest? pending;
    private Guid pendingId;

    public async Task<WatchReply> AskAsync(WatchRequest request, CancellationToken token)
    {
        var payload = WatchProtocol.Encode(request);
        if (payload.Length > WatchProtocol.MaximumPayload)
            throw new ArgumentException("本次资料过长，请缩短内容或证据摘录。");
        var response = await pipe.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
            WorkerProtocol.PrivatePluginData, WatchProtocol.PluginId, Payload: payload), token);
        if (response.Code != "Ok" || response.Payload is null)
            throw new WatchAccessException(response.Code);
        var reply = WatchProtocol.Decode<WatchReply>(response.Payload);
        if (reply.Error is { } error) throw new ArgumentException(error);
        return reply;
    }

    public async Task SaveAsync(WatchRequest request, CancellationToken token)
    {
        if (pending != request)
        {
            pending = request;
            pendingId = Guid.NewGuid();
        }
        try
        {
            await AskAsync(request with { ChangeId = pendingId }, token);
            pending = null;
        }
        catch (ArgumentException)
        {
            pending = null;
            throw;
        }
    }
}

internal sealed class WatchAccessException(string code) : IOException(code)
{
    public string Code { get; } = code;
}
