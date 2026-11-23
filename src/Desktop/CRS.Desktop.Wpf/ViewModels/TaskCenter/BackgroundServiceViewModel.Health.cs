using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Timing;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    [ObservableProperty] private string snapshotTime = "尚未取得状态快照";
    [ObservableProperty] private string healthLabel = "等待连接";
    [ObservableProperty] private string healthTone = "Warning";
    [ObservableProperty] private string serviceStatus = "尚未查询后台服务。";
    [ObservableProperty] private bool isCheckingService;
    private Task<BackgroundResponse?>? healthRead;

    [RelayCommand]
    private async Task RefreshServiceStatusAsync() => await ReadServiceHealthAsync();

    // 健康查询独立于任务读写门槛。计划刷新和状态页同时查询时共用这次采样。
    private Task<BackgroundResponse?> ReadServiceHealthAsync() => healthRead is { IsCompleted: false }
        ? healthRead : healthRead = QueryServiceHealthAsync();

    private async Task<BackgroundResponse?> QueryServiceHealthAsync()
    {
        if (lifetime.IsCancellationRequested) return null;
        IsCheckingService = true;
        try
        {
            var response = await client.QueryAsync("GetHealth", lifetime);
            lifetime.ThrowIfCancellationRequested();
            if (response.Code != "Ok" || response.Health is not { } sample)
            {
                SetServiceUnavailable("后台没有返回可用的健康状态。请检查配套服务版本后刷新。");
                return response;
            }

            (HealthLabel, HealthTone, ServiceStatus) = sample.State switch
            {
                "Running" => ("运行正常", "Success", "后台服务正常。关闭或锁定桌面后，后台任务仍会继续运行。"),
                "Degraded" => ("部分服务异常", "Warning", "后台服务仍在运行，部分插件或来源暂不可用。"),
                "Maintenance" => ("后台维护中", "Warning", "服务正在维护，请稍后刷新查看。"),
                "StorageFault" => ("后台存储故障", "Error", "后台已停止启动新任务。请修复存储并重启服务后核对。"),
                "UnresponsivePlugin" or "PluginUnresponsive" => ("插件未响应", "Error", "后台服务正在安全停止。"),
                _ => ("状态未确认", "Warning", $"后台报告的状态为 {sample.State}。")
            };
            SnapshotTime = $"状态采样 {BeijingClock.Normalize(sample.ObservedUtc):yyyy-MM-dd HH:mm:ss}（北京时间）";
            return response;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
        catch (Exception)
        {
            SetServiceUnavailable("无法确认后台服务状态。请检查服务是否已启动，然后刷新。");
            return null;
        }
        finally { IsCheckingService = false; }
    }

    private void SetServiceUnavailable(string message)
    {
        HealthLabel = "无法确认服务状态";
        HealthTone = "Warning";
        SnapshotTime = "本次未取得健康快照";
        ServiceStatus = message;
    }
}
