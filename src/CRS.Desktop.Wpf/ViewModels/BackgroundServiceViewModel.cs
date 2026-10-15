using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

/// <summary>仅通过本机 IPC 请求公开任务状态；服务停止后保留带采样时间的内存快照。</summary>
public partial class BackgroundServiceViewModel(CancellationToken lifetime, BackgroundPipeClient? serviceClient = null) : ObservableObject
{
    private readonly BackgroundPipeClient client = serviceClient ?? new();
    private BackgroundRequest? uncertainRequest;
    private string healthState = "Unknown";
    private string? historyCursor;
    private string? jobsCursor;
    public ObservableCollection<TaskDefinition> Jobs { get; } = [];
    public ObservableCollection<ExecutionRecord> History { get; } = [];
    /// <summary>日志始终绑定某次执行，查询管道与主页面共用同一服务和会话取消令牌。</summary>
    public ExecutionLogViewModel CreateExecutionLog(ExecutionRecord record)=>new(record,client,lifetime);
    [ObservableProperty] private string status = "正在查询独立后台服务。";
    [ObservableProperty] private string snapshotTime = "尚未取得状态快照";
    [ObservableProperty] private TaskDefinition? selected;
    [ObservableProperty] private string cron = "0 * * * * ?";
    [ObservableProperty] private string timeZoneId = "China Standard Time";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string historyPlugin = "crs.national-team";
    [ObservableProperty] private string historyJob = "";
    private string? appliedPlugin = "crs.national-team";
    private string? appliedJob;
    // 下拉建议允许补充手工任务编号，不把筛选范围限制为当前页。
    public ObservableCollection<string> HistoryPlugins { get; } = ["", "crs.national-team", "crs.heartbeat"];
    public ObservableCollection<string> HistoryJobs { get; } = [""];
    [RelayCommand]
    private Task FilterHistoryAsync() => WithStatusAsync(async () =>
    {
        appliedPlugin = string.IsNullOrWhiteSpace(HistoryPlugin) ? null : HistoryPlugin.Trim();
        appliedJob = string.IsNullOrWhiteSpace(HistoryJob) ? null : HistoryJob.Trim();
        historyCursor = null; History.Clear();
        await LoadAsync(); Status = "已按插件和任务筛选执行记录。";
    });
    public bool CanChangeHistory=>!Busy;
    partial void OnBusyChanged(bool value)=>OnPropertyChanged(nameof(CanChangeHistory));
    partial void OnSelectedChanged(TaskDefinition? value)
    {
        if (value is not null) { Cron = value.Cron; TimeZoneId = value.TimeZoneId; }
    }
    [RelayCommand]
    private Task RefreshAsync() => WithStatusAsync(async () => { await LoadAsync(); Status = healthState switch
        { "Running" => "后台服务正常；公开任务独立于窗口与税务登录运行。", "StorageFault" => "后台存储故障，已停止新任务；请修复并重启服务核验。", "UnresponsivePlugin" => "插件未响应取消，服务正在安全停止。", _ => "后台服务状态异常：" + healthState }; });
    private async Task LoadAsync()
    {
        var health = await client.QueryAsync("GetHealth", lifetime);
        healthState = health.Health?.State ?? "Unknown";
        if (healthState is "StorageFault" or "UnresponsivePlugin") return;
        var plans = await client.QueryAsync("GetJobs", lifetime);
        var history = await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory",PluginId:appliedPlugin,JobId:appliedJob),lifetime);
        lifetime.ThrowIfCancellationRequested();
        if (history.Code == "UnknownCommand") { Status = "后台服务版本不支持插件和任务组合筛选，请更新服务。"; throw new InvalidOperationException("服务不支持历史筛选。"); }
        if (health.Code != "Ok" || plans.Code != "Ok" || history.Code != "Ok") throw new InvalidOperationException("服务协议不兼容。");
        historyCursor = history.NextCursor;
        jobsCursor = plans.NextCursor;
        var key = Selected; Jobs.Clear(); foreach (var row in plans.Jobs ?? []) Jobs.Add(row);
        History.Clear(); foreach (var row in history.History ?? []) History.Add(row);
        // 建议保留已见过的任务；空白表示全部，也可输入未加载的历史编号。
        foreach (var row in Jobs.Select(x => (x.PluginId,x.JobId)).Concat(History.Select(x => (x.PluginId,x.JobId))))
        {
            if (!HistoryPlugins.Contains(row.PluginId)) HistoryPlugins.Add(row.PluginId);
            if (!HistoryJobs.Contains(row.JobId)) HistoryJobs.Add(row.JobId);
        }
        Selected = Jobs.FirstOrDefault(x => x.PluginId == key?.PluginId && x.JobId == key.JobId) ?? Jobs.FirstOrDefault();
        SnapshotTime = "状态采样：" + health.Health?.ObservedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + health.Health?.State;
    }
    [RelayCommand]
    private Task MoreJobsAsync() => WithStatusAsync(async () =>
    {
        if (jobsCursor is null) { Status = "没有更多计划。"; return; }
        var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetJobs", Cursor: jobsCursor), lifetime);
        if (response.Code != "Ok") { Status = "计划读取失败：" + response.Code; return; }
        foreach (var row in response.Jobs ?? []) if (!Jobs.Any(x => x.PluginId == row.PluginId && x.JobId == row.JobId)) Jobs.Add(row);
        jobsCursor = response.NextCursor; Status = jobsCursor is null ? "已读取全部计划。" : "已加载下一页计划。";
    });
    [RelayCommand]
    private Task MoreHistoryAsync() => WithStatusAsync(async () =>
    {
        if (historyCursor is null) { Status = "没有更多执行记录。"; return; }
        var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetFilteredHistory", PluginId:appliedPlugin,JobId:appliedJob,Cursor: historyCursor), lifetime);
        if (response.Code != "Ok") { Status = "历史读取失败：" + response.Code; return; }
        foreach (var row in response.History ?? []) if (!History.Any(x => x.ExecutionId == row.ExecutionId)) History.Add(row);
        historyCursor = response.NextCursor; Status = historyCursor is null ? "已读取全部可用历史。" : "已加载下一页历史。";
    });
    [RelayCommand]
    private Task RunNowAsync() => MutateAsync("RunNow");
    [RelayCommand]
    private Task PauseAsync() => MutateAsync("Pause");
    [RelayCommand]
    private Task ResumeAsync() => MutateAsync("Resume");
    [RelayCommand]
    private Task SaveAsync() => MutateAsync("ChangeSchedule");
    [RelayCommand]
    private Task CancelRunAsync() => MutateAsync("CancelRun");
    private Task MutateAsync(string command) => WithStatusAsync(async () =>
    {
        if (Selected is null) return;
        if (uncertainRequest is not null) { Status = "上次请求尚未确认，请先使用“确认上次请求”核对，避免误执行其它操作。"; return; }
        // 响应不确定时沿用同一个请求，防止重复点击产生另一次独立执行。
        uncertainRequest ??= new(WorkerProtocol.Version, Guid.NewGuid(), command, Selected.PluginId, Selected.JobId, Cron, TimeZoneId, Selected.Revision);
        var response = await client.SendAsync(uncertainRequest, lifetime);
        uncertainRequest = null;
        await LoadAsync();
        Status = response.Code switch
        {
            "Ok" => "设置已保存。", "Accepted" => "服务已接受请求，请刷新查看执行结果。",
            "Forbidden" => "当前 Windows 身份没有管理权限，请使用管理员身份运行管理端。",
            "RetryDeferred" => "任务处于退避期，暂缓执行。", "AlreadyRunning" => "该任务正在运行或等待执行。",
            "InvalidOrStaleRequest" => "计划输入无效或已被更改，请核对后重试。",
            "AcceptedNeedsReconcile" => "计划已保存，服务正在重建触发器。", _ => "服务返回：" + response.Code
        };
    });
    [RelayCommand]
    private Task ConfirmPendingAsync() => WithStatusAsync(async () =>
    {
        if (uncertainRequest is null) { Status = "没有待确认请求。"; return; }
        var response = await client.SendAsync(uncertainRequest, lifetime);
        uncertainRequest = null; await LoadAsync(); Status = "上次请求已确认：" + response.Code;
    });
    private async Task WithStatusAsync(Func<Task> action)
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        Busy = true;
        try { await action(); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { Status = "后台服务不可用或响应超时；所列内容为上次状态快照。"; }
        catch (OperationCanceledException) { }
        catch (Exception) { Status = "后台服务不可用或版本不兼容；请检查服务状态并更新到支持任务筛选的版本。所列内容为上次状态快照。"; }
        finally { Busy = false; }
    }
}
