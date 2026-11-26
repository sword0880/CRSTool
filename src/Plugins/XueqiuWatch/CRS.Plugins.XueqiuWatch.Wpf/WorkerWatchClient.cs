using System.IO;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Ipc;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed partial class WorkerWatchClient
{
    private readonly BackgroundPipeClient pipe = new();
    private WatchRequest? pending;
    private Guid pendingId;

    public async Task<WatchReply> AskAsync(WatchRequest request, CancellationToken token)
    {
        var payload = WatchProtocol.Encode(request);
        if (payload.Length > WatchProtocol.MaximumPayload)
            throw new ArgumentException("本次资料过长，请缩短内容或证据摘录。");
        var message = new BackgroundRequest(WorkerProtocol.Version, Guid.NewGuid(),
            WorkerProtocol.PrivatePluginData, WatchProtocol.PluginId, Payload: payload);
        var response = await pipe.SendAsync(message, token);
        if (IsBusy(response.Code))
        {
            using var retry = CancellationTokenSource.CreateLinkedTokenSource(token);
            retry.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                do
                {
                    await Task.Delay(250, retry.Token);
                    response = await pipe.SendAsync(message, retry.Token);
                } while (IsBusy(response.Code));
            }
            catch (OperationCanceledException) when (retry.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new WatchAccessException(response.Code);
            }
        }
        if (response.Code != "Ok" || response.Payload is null)
            throw new WatchAccessException(response.Code);
        var reply = WatchProtocol.Decode<WatchReply>(response.Payload);
        if (reply.Error is { } error) throw new ArgumentException(error);
        return reply;
    }

    private static bool IsBusy(string code) => code is "RateLimited" or "Busy" or "PluginBusy";

    public Task SetStockHiddenAsync(Security stock, bool hidden, CancellationToken token) =>
        SaveAsync(new("stock-visibility-save") { StockVisibility = new(stock, hidden) }, token);

    internal async Task<string> StartBackfillAsync(string account, CancellationToken token)
    {
        var leads = (await AskAsync(new("leads", new(Account: account)), token)).Leads;
        if (leads?.BackfillSupported != true) return "作者已保存。当前 Worker 版本尚未支持 30 天回补。";
        if (!leads.Coverage.Any(row => row.Account == account && row.BackfillPending)) return "作者已保存。";

        var run = new BackgroundRequest(WorkerProtocol.Version, Guid.NewGuid(), "RunNow",
            WatchProtocol.PluginId, WatchProtocol.LeadJobId);
        try
        {
            var response = await pipe.SendAsync(run, token);
            if (response.Code == "JobUnavailable")
            {
                string? cursor = null;
                do
                {
                    var options = await pipe.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
                        WorkerProtocol.GetPluginPlanOptions, Cursor: cursor), token);
                    if (options.Code != "Ok") break;
                    var plan = options.Jobs?.SingleOrDefault(job =>
                        job.PluginId == WatchProtocol.PluginId && job.JobId == WatchProtocol.LeadJobId);
                    if (plan is not null)
                    {
                        if (plan.Revision == 0)
                            await pipe.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), WorkerProtocol.AddPluginPlan,
                                plan.PluginId, plan.JobId, Cron: plan.Cron, Payload: "Paused"), token);
                        response = await pipe.SendAsync(run, token);
                        break;
                    }
                    cursor = options.NextCursor;
                } while (cursor is not null);
            }
            if (response.Code is "Accepted" or "AlreadyRunning")
                return "作者已保存。近 30 天回补已进入采集队列。";
        }
        catch (BackgroundServiceException) when (!token.IsCancellationRequested)
        {
            return "作者已保存。回补已登记，等待 Worker 受理。";
        }
        return "作者已保存。回补已登记，等待下一次采集。";
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
