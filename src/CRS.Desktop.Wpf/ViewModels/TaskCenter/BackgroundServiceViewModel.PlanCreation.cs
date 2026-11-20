using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    public bool CanAddPluginPlan => !Busy && UncertainRequest is null && !lifetime.IsCancellationRequested;

    internal async Task<IReadOnlyList<PluginPlanOption>?> ReadPluginPlanOptionsAsync()
    {
        List<PluginPlanOption>? options = null;
        await WithStatusAsync(async () =>
        {
            if (UncertainRequest is not null)
            {
                Status = "请先核对上次操作的结果。";
                return;
            }
            var rows = new List<TaskDefinition>();
            string? cursor = null;
            do
            {
                var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
                    WorkerProtocol.GetPluginPlanOptions, Cursor: cursor), lifetime);
                if (response.Code != "Ok")
                {
                    Status = response.Code == "UnknownCommand" ? "请更新后台服务以使用“添加计划”。" : "无法读取插件任务，请稍后刷新。";
                    return;
                }
                if (response.Jobs is null || response.NextCursor is not null &&
                    string.CompareOrdinal(response.NextCursor, cursor) <= 0)
                    throw new System.IO.InvalidDataException("插件任务清单无效，请核对前后台版本。");
                rows.AddRange(response.Jobs);
                cursor = response.NextCursor;
            } while (cursor is not null);
            options = rows.OrderBy(x => x.Revision != 0).ThenBy(x => x.Name, StringComparer.CurrentCulture)
                .Select(plan => new PluginPlanOption(plan)).ToList();
            const string xueqiuPluginId = "crs.xueqiu-watch";
            if (rows.Any(plan => plan.PluginId == xueqiuPluginId)) return;

            var health = await ReadServiceHealthAsync();
            if (health is { Code: "Ok", Plugins: { } plugins } &&
                plugins.Any(plugin => plugin is { PluginId: xueqiuPluginId, State: "Started" }))
            {
                options.Insert(0, new("同步雪球发言", "雪球观点追踪", "待接入数据源", null,
                    "自动同步需要接入覆盖观察名单的授权数据源。"));
            }
        });
        return options;
    }

    internal async Task<(bool Saved, string Message)> AddPluginPlanAsync(TaskDefinition template, string cron, bool enabled)
    {
        if (Busy || lifetime.IsCancellationRequested) return (false, "请等待正在处理的请求完成。");
        BackgroundResponse? result = null;
        await MutateAsync(WorkerProtocol.AddPluginPlan, new(WorkerProtocol.Version, Guid.NewGuid(),
            WorkerProtocol.AddPluginPlan, template.PluginId, template.JobId, Cron: cron, Payload: enabled ? "Enabled" : "Paused"),
            response => result = response);
        return (result?.Code is "Ok" or "AcceptedNeedsReconcile", Status);
    }

    private void SelectCreatedPlan(BackgroundResponse response)
    {
        if (response.Jobs is not [var plan] || plan.Revision == 0) return;
        var existing = Jobs.FirstOrDefault(row => row.PluginId == plan.PluginId && row.JobId == plan.JobId);
        if (existing is null) Jobs.Add(plan);
        Selected = existing ?? plan;
        UpdateHistorySuggestions();
    }
}

internal sealed record PluginPlanOption(string Name, string Plugin, string Status, TaskDefinition? Plan, string? Detail = null)
{
    internal PluginPlanOption(TaskDefinition plan)
        : this(plan.Name, plan.PluginId, plan.Revision == 0 ? "可添加" : "已添加", plan) { }
}
