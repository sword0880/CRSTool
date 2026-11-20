using CRS.Timing;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using Quartz;

namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    private static bool IsSchedulable(HostedBackgroundJob job) => !job.Descriptor.ManualOnly && !job.Descriptor.Diagnostic;
    private static bool WaitsForPlanCreation(HostedBackgroundJob job) => IsSchedulable(job) && job.Descriptor.RequiresPlanCreation;

    private BackgroundResponse ReadPluginPlanOptions(BackgroundRequest request)
    {
        var plans = repository.Plans().ToDictionary(Key);
        var matches = catalog.Jobs.Where(job => IsSchedulable(job) && catalog.CanDispatch(job.PluginId))
            .Select(job => (Job: job, Cursor: job.PluginId + ":" + job.Descriptor.JobId))
            .Where(row => request.Cursor is null || string.CompareOrdinal(row.Cursor, request.Cursor) > 0)
            .OrderBy(row => row.Cursor, StringComparer.Ordinal)
            .Take(request.PageSize + 1).ToArray();
        var page = matches.Take(request.PageSize).Select(row =>
        {
            var job = row.Job;
            // 0 表示任务已由插件提供，尚未保存计划。已保存计划的修订号从 1 开始。
            return plans.TryGetValue(new JobKey(job.Descriptor.JobId, job.PluginId), out var plan)
                ? plan : new TaskDefinition(job.PluginId, job.Descriptor.JobId, job.Descriptor.Name,
                    job.Descriptor.DefaultCron, BeijingClock.ZoneId, job.Descriptor.EnabledByDefault, 0, false);
        }).ToArray();
        return new(WorkerProtocol.Version, request.RequestId, "Ok", Jobs: page,
            NextCursor: matches.Length > page.Length ? matches[page.Length - 1].Cursor : null);
    }

    private async Task<BackgroundResponse> AddPluginPlanAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token)
    {
        BackgroundResponse Reply(string code, CommandReceipt? receipt = null, TaskDefinition? plan = null) =>
            new(WorkerProtocol.Version, request.RequestId, code, Jobs: plan is null ? null : [plan], Receipt: receipt);

        if (request.Cron is not { } cron || request.Payload is not ("Enabled" or "Paused"))
            return Reply("InvalidRequest");
        try { Validate(cron, BeijingClock.ZoneId); }
        catch (ArgumentException) { return Reply("InvalidOrStaleRequest"); }

        var job = catalog.Jobs.FirstOrDefault(x => x.PluginId == request.PluginId && x.Descriptor.JobId == request.JobId);
        if (job is null || !IsSchedulable(job) || !catalog.CanDispatch(job.PluginId))
            return Reply("JobUnavailable");

        CommandReceipt receipt;
        try { receipt = repository.AcceptCommand(request, caller.Sid); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return Reply("InvalidOrStaleRequest"); }

        var plan = new TaskDefinition(job.PluginId, job.Descriptor.JobId, job.Descriptor.Name, cron,
            BeijingClock.ZoneId, request.Payload == "Enabled", 1, true);
        var added = receipt.State == "Pending";
        var code = added ? repository.AddPlan(plan, request.RequestId) : receipt.Code;
        receipt = repository.FindCommand(request.RequestId, caller.Sid)!;
        if (code != "Ok") return Reply(code, receipt);

        // 计划与回执已提交。调度器应用失败时保留待生效标记，交给现有协调流程重试。
        if (added)
        {
            try { await ApplyAsync(plan, token); }
            catch (SchedulerException) { }
        }
        var current = repository.Plans().Single(x => x.PluginId == plan.PluginId && x.JobId == plan.JobId);
        return Reply(current.NeedsReconcile ? "AcceptedNeedsReconcile" : "Ok", receipt, await DescribePlanAsync(current, token));
    }
}
