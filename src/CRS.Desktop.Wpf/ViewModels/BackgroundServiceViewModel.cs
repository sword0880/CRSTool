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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnconfirmedRequest))] private BackgroundRequest? uncertainRequest;
    private string healthState = "Unknown";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMoreJobs))] private string? jobsCursor;
    public bool HasMoreJobs=>JobsCursor is not null;
    public bool HasUnconfirmedRequest=>UncertainRequest is not null&&!Busy;
    public ObservableCollection<TaskDefinition> Jobs { get; } = [];
    public ObservableCollection<ExecutionRecord> History { get; } = [];
    public ObservableCollection<BackgroundPendingRequest> PendingRequests { get; } = [];
    [ObservableProperty] private string pendingStatus="尚未查询等待开始的请求。";
    /// <summary>日志始终绑定某次执行，查询管道与主页面共用同一服务和会话取消令牌。</summary>
    public ExecutionLogViewModel CreateExecutionLog(ExecutionRecord record)=>new(record,client,lifetime);
    public CancellationToken Lifetime=>lifetime;
    private bool scheduleSaved;
    public async Task<(bool Saved,string Message)> SaveScheduleAsync(TaskDefinition plan,string expression,string zone)
    {
        scheduleSaved=false;
        if(Busy)return (false,"正在处理其它请求，请稍后保存。");
        await MutateAsync("ChangeSchedule",new(WorkerProtocol.Version,Guid.NewGuid(),"ChangeSchedule",plan.PluginId,plan.JobId,expression,zone,plan.Revision));
        return (scheduleSaved,Status);
    }
    [ObservableProperty] private string status = "正在查询独立后台服务。";
    [ObservableProperty] private string snapshotTime = "尚未取得状态快照";
    // 状态页只消费可观察的语义状态，避免依据提示字符串或上次快照猜测服务是否在线。
    [ObservableProperty] private string healthLabel = "等待连接";
    [ObservableProperty] private string healthTone = "Warning";
    [ObservableProperty] private TaskDefinition? selected;
    [ObservableProperty] private string cron = "0 * * * * ?";
    [ObservableProperty] private string timeZoneId = "China Standard Time";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string historyPlugin = "";
    [ObservableProperty] private string historyJob = "";
    private string? appliedPlugin;
    private string? appliedJob;
    private string? appliedResultFilter;
    [ObservableProperty] private bool onlyIssues;
    // 下拉建议允许补充手工任务编号，不把筛选范围限制为当前页。
    public ObservableCollection<string> HistoryPlugins { get; } = ["", "crs.national-team", "crs.heartbeat"];
    public ObservableCollection<string> HistoryJobs { get; } = [""];
    [RelayCommand]
    private Task FilterHistoryAsync() => WithStatusAsync(async () =>
    {
        if(HistoryStartDate is not {} start||HistoryEndDate is not {} end||start.Date>end.Date)
        {
            History.Clear();PendingRequests.Clear();PendingStatus="尚未查询等待开始的请求。";
            HistorySummary=HistoryStartDate is null||HistoryEndDate is null?"请选择开始日期和结束日期。":"开始日期不能晚于结束日期。";
            Status=HistorySummary;return;
        }
        appliedHistoryStartDate=DateOnly.FromDateTime(start);
        appliedHistoryEndDate=DateOnly.FromDateTime(end);
        appliedPlugin = string.IsNullOrWhiteSpace(HistoryPlugin) ? null : HistoryPlugin.Trim();
        appliedJob = string.IsNullOrWhiteSpace(HistoryJob) ? null : HistoryJob.Trim();
        appliedResultFilter = OnlyIssues ? "Issues" : null;
        History.Clear();PendingRequests.Clear();
        Status="正在查询执行记录和等待开始的请求……";HistorySummary=$"正在查询 {appliedHistoryStartDate:yyyy-MM-dd} 至 {appliedHistoryEndDate:yyyy-MM-dd} 的记录……";
        await LoadHistoryAsync();
        Status = OnlyIssues
            ? History.Count==0
                ? "异常执行筛选已启用，但没有匹配记录。来源冷却或插件启动失败可能没有执行记录，请查看后台服务状态。"
                : "已按日期、插件和任务筛选异常记录。"
            : HistorySummary;
    });
    /// <summary>底栏直达执行记录；正在刷新时排队应用导航条件，避免忙碌状态吞掉用户点击。</summary>
    public async Task OpenExecutionHistoryAsync(bool issues)
    {
        while(Busy)
        {
            lifetime.ThrowIfCancellationRequested();
            await Task.Delay(80,lifetime);
        }
        lifetime.ThrowIfCancellationRequested();
        HistoryPlugin="";
        HistoryStartDate=HistoryEndDate=CRS.Timing.BeijingClock.Today;
        HistoryJob="";
        OnlyIssues=issues;
        History.Clear();PendingRequests.Clear();PendingStatus="尚未查询等待开始的请求。";
        HistorySummary="默认选择北京时间当天，请点击“查询记录”。";
    }

    public bool CanChangeHistory=>!Busy;
    partial void OnBusyChanged(bool value)
    {OnPropertyChanged(nameof(CanChangeHistory));OnPropertyChanged(nameof(HasUnconfirmedRequest));}
    partial void OnSelectedChanged(TaskDefinition? value)
    {
        if (value is not null) { Cron = value.Cron; TimeZoneId = value.TimeZoneId; }
    }
    [RelayCommand]
    private Task RefreshAsync() => WithStatusAsync(async () => { await LoadAsync(); Status = healthState switch
        { "Running" => "后台服务正常；公开任务独立于窗口与税务登录运行。", "StorageFault" => "后台存储故障，已停止新任务；请修复并重启服务核验。", "UnresponsivePlugin" => "插件未响应取消，服务正在安全停止。", _ => "后台服务状态异常：" + healthState }; });
    private async Task LoadAsync()
    {
        // 状态刷新不查询执行记录；历史只由用户的“查询记录”命令读取。
        var activity=RefreshTaskActivityAsync();
        await activity;
        var health = await client.QueryAsync("GetHealth", lifetime);
        healthState = health.Health?.State ?? "Unknown";
        HealthLabel = healthState switch
        {
            "Running" => "运行正常",
            "StorageFault" => "后台存储故障",
            "UnresponsivePlugin" => "插件未响应 · 正在停止",
            _ => "状态未确认"
        };
        HealthTone = healthState switch
        {
            "Running" when health.Code == "Ok" => "Success",
            "StorageFault" or "UnresponsivePlugin" => "Error",
            _ => "Warning"
        };
        // 故障同样需要显示真实采样时刻；没有新健康采样时保留上次时间。
        if (health.Health is { } sample)
            SnapshotTime = "状态采样：" + sample.ObservedUtc.ToString("yyyy-MM-dd HH:mm:ss") + "（北京时间） · " + sample.State;
        if (healthState is "StorageFault" or "UnresponsivePlugin") return;
        var plans = await client.QueryAsync("GetJobs", lifetime);
        lifetime.ThrowIfCancellationRequested();
        if (health.Code != "Ok" || plans.Code != "Ok") throw new InvalidOperationException("服务协议不兼容。");
        JobsCursor = plans.NextCursor;
        var key = Selected; Jobs.Clear(); foreach (var row in plans.Jobs ?? []) Jobs.Add(row);
        UpdateHistorySuggestions();
        Selected = Jobs.FirstOrDefault(x => x.PluginId == key?.PluginId && x.JobId == key.JobId) ?? Jobs.FirstOrDefault();
    }
    private void UpdateHistorySuggestions()
    {
        // 建议保留已见过的任务；空白表示全部，也可输入未加载的历史编号。
        foreach (var row in Jobs.Select(x => (x.PluginId,x.JobId)).Concat(History.Select(x => (x.PluginId,x.JobId))))
        {
            if (!HistoryPlugins.Contains(row.PluginId)) HistoryPlugins.Add(row.PluginId);
            if (!HistoryJobs.Contains(row.JobId)) HistoryJobs.Add(row.JobId);
        }
    }
    [RelayCommand]
    private Task MoreJobsAsync() => WithStatusAsync(async () =>
    {
        if (JobsCursor is null) { Status = "没有更多计划。"; return; }
        var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetJobs", Cursor: JobsCursor), lifetime);
        if (response.Code != "Ok") { Status = "计划读取失败：" + response.Code; return; }
        foreach (var row in response.Jobs ?? []) if (!Jobs.Any(x => x.PluginId == row.PluginId && x.JobId == row.JobId)) Jobs.Add(row);
        JobsCursor = response.NextCursor; Status = JobsCursor is null ? "已读取全部计划。" : "已加载下一页计划。";
    });
    public static bool IsIssueResult(string result) =>
        result is not ("Succeeded" or "NotPublished" or "Running" or "Cancelled");

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
    private Task MutateAsync(string command,BackgroundRequest? explicitRequest=null) => WithStatusAsync(async () =>
    {
        if (Selected is null && explicitRequest is null) return;
        if (UncertainRequest is not null) { Status = "上次请求尚未确认，请先使用“重新确认请求”核对，避免误执行其它操作。"; return; }
        // 响应不确定时沿用同一个请求，防止重复点击产生另一次独立执行。
        UncertainRequest = explicitRequest ?? new(WorkerProtocol.Version, Guid.NewGuid(), command, Selected!.PluginId, Selected.JobId, Cron, TimeZoneId, Selected.Revision);
        var response = await client.SendAsync(UncertainRequest, lifetime);
        UncertainRequest = null;
        scheduleSaved=command=="ChangeSchedule" && response.Code is "Ok" or "AcceptedNeedsReconcile";
        await LoadAsync();
        Status = response.Code switch
        {
            "Ok" => "设置已保存。", "Accepted" => "服务已接受请求，请刷新查看执行结果。",
            "Forbidden" => "当前 Windows 身份没有管理权限，请使用管理员身份运行管理端。",
            "RetryDeferred" => "任务处于退避期，暂缓执行。", "AlreadyRunning" => "该任务正在运行或等待执行。",
            "InvalidOrStaleRequest" => "计划输入无效或已被更改，请核对后重试。",
            "AcceptedNeedsReconcile" => "计划已保存，等待后台应用；后台会自动重试，生效后自动清除“计划待生效”标记。", _ => "服务返回：" + response.Code
        };
    });
    [RelayCommand]
    private Task ConfirmPendingAsync() => WithStatusAsync(async () =>
    {
        if (UncertainRequest is null) { Status = "没有待确认请求。"; return; }
        var response = await client.SendAsync(UncertainRequest, lifetime);
        UncertainRequest = null; await LoadAsync(); Status = "上次请求已确认：" + response.Code;
    });
    private async Task WithStatusAsync(Func<Task> action)
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        Busy = true;
        try { await action(); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { HealthLabel = "连接超时"; HealthTone = "Warning"; PendingStatus="待执行请求未确认；列表为上次快照。";Status = "后台服务不可用或响应超时；所列内容为上次状态快照。"; }
        catch (OperationCanceledException) { }
        catch (System.IO.InvalidDataException error)
        {
            Status=error.Message;HealthLabel="状态未确认";HealthTone="Warning";
            History.Clear();PendingRequests.Clear();PendingStatus="待执行请求未确认。";HistorySummary="执行记录未确认，请重新查询。";
        }
        catch (Exception) { HealthLabel = "无法确认服务状态"; HealthTone = "Warning";PendingStatus="待执行请求未确认；列表为上次快照。"; Status = "后台服务不可用或版本不兼容；请检查服务状态并更新到支持任务筛选的版本。所列内容为上次状态快照。"; }
        finally { Busy = false; }
    }
}
