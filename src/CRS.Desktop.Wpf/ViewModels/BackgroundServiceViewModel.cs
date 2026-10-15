using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Background.Abstractions;
using CRS.Background.Ipc;

namespace CRS.DesktopClient.ViewModels;

/// <summary>仅通过本机 IPC 请求公开任务状态；服务停止后保留带采样时间的内存快照。</summary>
public partial class BackgroundServiceViewModel(CancellationToken lifetime) : ObservableObject
{
    private readonly BackgroundPipeClient client = new();
    public ObservableCollection<TaskDefinition> Jobs { get; } = [];
    public ObservableCollection<ExecutionRecord> History { get; } = [];
    [ObservableProperty] private string status = "正在查询独立后台服务。";
    [ObservableProperty] private string snapshotTime = "尚未取得状态快照";
    [ObservableProperty] private TaskDefinition? selected;
    [ObservableProperty] private string cron = "0 * * * * ?";
    [ObservableProperty] private string timeZoneId = "China Standard Time";
    [ObservableProperty] private bool busy;
    partial void OnSelectedChanged(TaskDefinition? value)
    {
        if (value is not null) { Cron = value.Cron; TimeZoneId = value.TimeZoneId; }
    }
    [RelayCommand]
    private Task RefreshAsync() => WithStatusAsync(async () => { await LoadAsync(); Status = "后台服务正常；公开任务独立于窗口与税务登录运行。"; });
    private async Task LoadAsync()
    {
        var health = await client.QueryAsync("GetHealth", lifetime);
        var plans = await client.QueryAsync("GetJobs", lifetime);
        var history = await client.QueryAsync("GetHistory", lifetime);
        lifetime.ThrowIfCancellationRequested();
        if (health.Code != "Ok" || plans.Code != "Ok" || history.Code != "Ok") throw new InvalidOperationException("服务协议不兼容。");
        var key = Selected; Jobs.Clear(); foreach (var row in plans.Jobs ?? []) Jobs.Add(row);
        History.Clear(); foreach (var row in history.History ?? []) History.Add(row);
        Selected = Jobs.FirstOrDefault(x => x.PluginId == key?.PluginId && x.JobId == key.JobId) ?? Jobs.FirstOrDefault();
        SnapshotTime = "状态采样：" + health.Health?.ObservedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + health.Health?.State;
    }
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
        var response = await client.SendAsync(new(1, Guid.NewGuid(), command, Selected.PluginId, Selected.JobId, Cron, TimeZoneId, Selected.Revision), lifetime);
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
    private async Task WithStatusAsync(Func<Task> action)
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        Busy = true;
        try { await action(); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { Status = "后台服务不可用或响应超时；所列内容为上次状态快照。"; }
        catch (OperationCanceledException) { }
        catch (Exception) { Status = "后台服务不可用或协议不兼容；请检查独立服务安装与启动状态。所列内容为上次状态快照。"; }
        finally { Busy = false; }
    }
}
