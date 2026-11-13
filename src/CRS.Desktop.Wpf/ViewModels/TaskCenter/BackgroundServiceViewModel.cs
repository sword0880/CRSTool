using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Maintenance;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

/// <summary>通过本机 IPC 请求公开任务与当前管理员可见的清理摘要；服务停止后保留并标明旧快照。</summary>
public partial class BackgroundServiceViewModel : ObservableObject
{
    private readonly CancellationToken lifetime;
    private readonly BackgroundPipeClient client;
    private readonly DataCleanupViewModel? cleanup;
    public BackgroundServiceViewModel(CancellationToken lifetime,BackgroundPipeClient? serviceClient=null,DataCleanupViewModel? cleanup=null)
    {
        this.lifetime=lifetime;client=serviceClient??new();this.cleanup=cleanup;
        maintenancePlan=new(cleanup);PlanRows.Add(maintenancePlan);
        Jobs.CollectionChanged+=OnJobsChanged;
        maintenancePlan.PropertyChanged+=(_,_)=>NotifyPlanCommands();
    }
    public DataCleanupViewModel? Cleanup=>cleanup;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnconfirmedRequest))] private BackgroundRequest? uncertainRequest;
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
    public async Task<(bool Saved,string Message)> SetScheduleEnabledAsync(TaskDefinition plan,bool enabled)
    {
        if(Busy||lifetime.IsCancellationRequested)return (false,"正在处理其它请求或会话已关闭，请稍后重试。");
        BackgroundResponse? result=null;
        var command=enabled?"Resume":"Pause";
        await MutateAsync(command,new(WorkerProtocol.Version,Guid.NewGuid(),command,plan.PluginId,plan.JobId,ExpectedRevision:plan.Revision),response=>result=response);
        return (result?.Code is "Ok" or "AcceptedNeedsReconcile",Status);
    }
    [ObservableProperty] private string status = "等待查询。";
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
    public ObservableCollection<string> HistoryPlugins { get; } = ["", DataCleanupTask.ServiceId, "crs.national-team", "crs.heartbeat"];
    public ObservableCollection<string> HistoryJobs { get; } = ["", DataCleanupTask.JobId];
    [RelayCommand]
    private Task FilterHistoryAsync() => WithStatusAsync(async () =>
    {
        if(HistoryStartDate is not {} start||HistoryEndDate is not {} end||start.Date>end.Date)
        {
            History.Clear();PendingRequests.Clear();PendingStatus="尚未查询等待开始的请求。";
            HistorySummary=HistoryStartDate is null||HistoryEndDate is null?"请选择开始日期和结束日期。":"开始日期不能晚于结束日期。";
            return;
        }
        appliedHistoryStartDate=DateOnly.FromDateTime(start);
        appliedHistoryEndDate=DateOnly.FromDateTime(end);
        appliedPlugin = string.IsNullOrWhiteSpace(HistoryPlugin) ? null : HistoryPlugin.Trim();
        appliedJob = string.IsNullOrWhiteSpace(HistoryJob) ? null : HistoryJob.Trim();
        appliedResultFilter = OnlyIssues ? "Issues" : null;
        History.Clear();PendingRequests.Clear();
        HistorySummary=$"正在查询 {appliedHistoryStartDate:yyyy-MM-dd} 至 {appliedHistoryEndDate:yyyy-MM-dd} 的记录……";
        await LoadHistoryAsync();
    }, reportHistoryFailure: true);
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
    {OnPropertyChanged(nameof(CanChangeHistory));OnPropertyChanged(nameof(HasUnconfirmedRequest));NotifyPlanCommands();}
    partial void OnSelectedChanged(TaskDefinition? value)
    {
        if (value is not null) { Cron = value.Cron; TimeZoneId = value.TimeZoneId;SelectedPlan=PlanRows.FirstOrDefault(x=>x.Job==value); }
        else if(SelectedPlan?.IsMaintenance!=true)SelectedPlan=null;
        NotifyPlanCommands();
    }
    [RelayCommand]
    private Task RefreshAsync() => WithStatusAsync(async () => { await LoadAsync(); Status = ServiceStatus; });
    private async Task LoadAsync()
    {
        // 当前任务采样独立于历史查询。只有旧版计划需要额外补读最近启动时间。
        await RefreshTaskActivityAsync();
        var health = await ReadServiceHealthAsync();
        lifetime.ThrowIfCancellationRequested();
        if (health?.Code != "Ok") throw new InvalidOperationException("服务健康查询未完成。");
        if (health.Health?.State is "StorageFault" or "UnresponsivePlugin" or "PluginUnresponsive") return;
        var plans = await client.QueryAsync("GetJobs", lifetime);
        lifetime.ThrowIfCancellationRequested();
        if (plans.Code != "Ok") throw new InvalidOperationException("服务协议不兼容。");
        JobsCursor = plans.NextCursor;
        var key = Selected;var keepMaintenance=SelectedPlan?.IsMaintenance==true; Jobs.Clear(); foreach (var row in plans.Jobs ?? []) Jobs.Add(row);
        UpdateHistorySuggestions();
        if(keepMaintenance)SelectedPlan=maintenancePlan;
        else Selected = Jobs.FirstOrDefault(x => x.PluginId == key?.PluginId && x.JobId == key.JobId) ?? Jobs.FirstOrDefault();
        await ReadLegacyPlanStartsAsync(PlanRows);
    }
    [RelayCommand]
    private Task MoreJobsAsync() => WithStatusAsync(async () =>
    {
        if (JobsCursor is null) { Status = "没有更多计划。"; return; }
        var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetJobs", Cursor: JobsCursor), lifetime);
        if (response.Code != "Ok") { Status = "计划读取失败：" + response.Code; return; }
        var previousCount = PlanRows.Count;
        foreach (var row in response.Jobs ?? []) if (!Jobs.Any(x => x.PluginId == row.PluginId && x.JobId == row.JobId)) Jobs.Add(row);
        UpdateHistorySuggestions();
        JobsCursor = response.NextCursor; Status = JobsCursor is null ? "已读取全部计划。" : "已加载下一页计划。";
        await ReadLegacyPlanStartsAsync(PlanRows.Skip(previousCount));
    });
    public static bool IsIssueResult(string result) =>
        result is not ("Succeeded" or "NotPublished" or "Running" or "Cancelled");

    [RelayCommand(CanExecute=nameof(CanRunSelectedPlan))]
    private async Task RunNowAsync()
    {
        if(SelectedPlan?.IsMaintenance==true)
        {
            if(!CanRunSelectedPlan())return;
            await cleanup!.SubmitTaskCommand.ExecuteAsync(null);
            Status=cleanup.TaskStatusKnown?"清理任务已交给后台，可在“当前任务”查看。":"清理任务受理状态未确认，请刷新核对。";
        }
        else await MutateAsync("RunNow");
    }
    [RelayCommand(CanExecute=nameof(CanUseSelectedPlan))]
    private Task PauseAsync() => MutateAsync("Pause");
    [RelayCommand(CanExecute=nameof(CanUseSelectedPlan))]
    private Task ResumeAsync() => MutateAsync("Resume");
    [RelayCommand(CanExecute=nameof(CanUseSelectedPlan))]
    private Task SaveAsync() => MutateAsync("ChangeSchedule");
    [RelayCommand(CanExecute=nameof(CanUseSelectedPlan))]
    private Task CancelRunAsync() => MutateAsync("CancelRun");
    private Task MutateAsync(string command,BackgroundRequest? explicitRequest=null,Action<BackgroundResponse>? onResponse=null) => WithStatusAsync(async () =>
    {
        if (Selected is null && explicitRequest is null) return;
        if (UncertainRequest is not null) { Status = "上次请求尚未确认，请先使用“重新确认请求”核对，避免误执行其它操作。"; return; }
        // 响应不确定时沿用同一个请求，防止重复点击产生另一次独立执行。
        UncertainRequest = explicitRequest ?? new(WorkerProtocol.Version, Guid.NewGuid(), command, Selected!.PluginId, Selected.JobId, Cron, TimeZoneId, Selected.Revision);
        var response = await client.SendAsync(UncertainRequest, lifetime);
        UncertainRequest = null;
        onResponse?.Invoke(response);
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
    private async Task WithStatusAsync(Func<Task> action, bool reportHistoryFailure = false)
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        Busy = true;
        try { await action(); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            PendingStatus = "待执行请求未确认。列表保留上次快照。";
            const string message = "后台服务不可用或响应超时。列表保留上次快照。";
            if (reportHistoryFailure) HistorySummary = message; else Status = message;
        }
        catch (OperationCanceledException) { }
        catch (System.IO.InvalidDataException error)
        {
            if(!reportHistoryFailure)Status=error.Message;
            History.Clear();PendingRequests.Clear();PendingStatus="待执行请求未确认。";HistorySummary="执行记录未确认，请重新查询。";
        }
        catch (Exception)
        {
            PendingStatus = "待执行请求未确认。列表保留上次快照。";
            const string message = "后台服务不可用或版本不兼容。请在「服务状态」页签检查连接。列表保留上次快照。";
            if (reportHistoryFailure) HistorySummary = message; else Status = message;
        }
        finally { Busy = false; }
    }
}
