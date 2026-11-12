using System.Text.Json;
using CRS.Common.Concurrency;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;

namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    private readonly Dictionary<(string Plugin, BackgroundQueryWorkload Kind, bool Ai), BoundedWorkAdmission> queryAdmissions = [];
    private int nationalTeamDesktopResearch;

    /// <summary>准入与排空由宿主统一管理，业务读取在全局锁之外执行。</summary>
    private async Task<BackgroundResponse> QueryPluginAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token)
    {
        BackgroundResponse Reply(string code, string? payload = null) =>
            new(WorkerProtocol.Version, request.RequestId, code, Payload: payload);

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = caller.Sid == AiGatewayWorkerEndpoint.ServiceSid;
        bool desktopResearch;
        BoundedWorkAdmission admission;
        await gate.WaitAsync(token);
        try
        {
            if (fault is not null) return Reply(fault);
            if (catalog.RequiresProcessRestart) return Reply("PluginUnresponsive");
            if (maintenance) return Reply("Maintenance");
            if (request.PluginId is null || request.Payload is null || request.Payload.Length > 4096)
                return Reply("InvalidRequest");
            if (!catalog.SupportsQueries(request.PluginId)) return Reply("UnknownQuery");

            var routing = catalog.QueryRouting(request.PluginId);
            BackgroundQueryWorkload kind;
            try
            {
                if (ai != AiGatewayWorkerRequestPolicy.IsSinglePageRoute(request.PluginId, request.Payload))
                    return Reply("Forbidden");
                kind = routing?.Classify(request.Payload) ?? BackgroundQueryWorkload.ResearchRead;
                if (ai && kind != BackgroundQueryWorkload.ResearchRead) return Reply("Forbidden");
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException)
            {
                return Reply("InvalidRequest");
            }

            // 前台有在途或排队研究读时拒绝新 AI 请求；已开始的 AI 只占自己的名额。
            if (ai && nationalTeamDesktopResearch > 0) return Reply("PluginBusy");
            var admissionKey = (request.PluginId, kind, ai);
            if (!queryAdmissions.TryGetValue(admissionKey, out admission!))
            {
                if (queryAdmissions.Count >= 128) return Reply("PluginBusy");
                admission = ai || routing is null
                    ? new(1, 0, TimeSpan.Zero)
                    : new(BackgroundQueryBudgets.Concurrency(kind), BackgroundQueryBudgets.Waiting(kind),
                        BackgroundQueryBudgets.Wait(kind));
                queryAdmissions.Add(admissionKey, admission);
            }
            desktopResearch = !ai && request.PluginId == AiGatewayWorkerRequestPolicy.PluginId &&
                kind == BackgroundQueryWorkload.ResearchRead;
            if (desktopResearch) nationalTeamDesktopResearch++;
            activeQueries.Add(completed);
        }
        finally { gate.Release(); }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, queryStopping.Token);
        try
        {
            using var lease = await admission.EnterAsync(cancellation.Token);
            using var aiDeadline = ai ? CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token) : null;
            aiDeadline?.CancelAfter(TimeSpan.FromSeconds(8));
            var execution = aiDeadline?.Token ?? cancellation.Token;
            var result = await catalog.QueryAsync(request.PluginId, request.Payload, execution);
            execution.ThrowIfCancellationRequested();
            return Reply("Ok", result);
        }
        catch (WorkAdmissionException) { return Reply("PluginBusy"); }
        catch (ArgumentException) { return Reply("UnknownQuery"); }
        catch (JsonException) { return Reply("InvalidRequest"); }
        catch (InvalidDataException) { return Reply("InvalidRequest"); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Reply("RequestTimedOut"); }
        catch (PluginLifecycleTimeoutException) { throw; } // 保留宿主对不合作插件的终止策略。
        catch (Exception error)
        {
            diagnostics?.Invoke("PluginQueryFailed", request.PluginId, error.GetType().Name);
            return Reply("PluginQueryFailed");
        }
        finally
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                if (desktopResearch) nationalTeamDesktopResearch--;
                activeQueries.Remove(completed);
                completed.SetResult();
            }
            finally { gate.Release(); }
        }
    }
}
