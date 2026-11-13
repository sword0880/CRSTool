using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;
using CRS.Maintenance;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

public partial class SettingsViewModel
{
    private readonly IWorkerLogSettingsGateway? workerLogSettingsGateway;
    private WorkerLogSettings? savedWorkerLogSettings;
    [ObservableProperty] private int workerLogRetentionDays = WorkerLogSettings.DefaultRetentionDays;
    [ObservableProperty] private string workerLogSettingsMessage = "打开本页时读取后台设置。";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditWorkerLogSettings))]
    [NotifyCanExecuteChangedFor(nameof(RefreshWorkerLogSettingsCommand), nameof(SaveCommand))]
    private bool workerLogSettingsBusy;

    public bool CanEditWorkerLogSettings => savedWorkerLogSettings is not null && !WorkerLogSettingsBusy;
    private bool HasWorkerLogChanges => savedWorkerLogSettings is { } saved && saved.RetentionDays != WorkerLogRetentionDays;

    private bool CanRefreshWorkerLogSettings() => workerLogSettingsGateway?.CanExecute == true && !WorkerLogSettingsBusy;

    [RelayCommand(CanExecute = nameof(CanRefreshWorkerLogSettings))]
    private Task RefreshWorkerLogSettingsAsync() => LoadWorkerLogSettingsAsync(force: true);

    internal async Task LoadWorkerLogSettingsAsync(bool force = false)
    {
        if (workerLogSettingsGateway is null || !workerLogSettingsGateway.CanExecute)
        {
            WorkerLogSettingsMessage = "请以管理员身份运行 CRS 并连接配套后台服务。";
            return;
        }
        if (WorkerLogSettingsBusy || (!force && savedWorkerLogSettings is not null)) return;
        WorkerLogSettingsBusy = true;
        try
        {
            var settings = await workerLogSettingsGateway.LoadLogSettingsAsync(CancellationToken.None);
            ApplyWorkerLogSettings(settings);
            WorkerLogSettingsMessage = $"后台当前保留 {settings.RetentionDays} 天。";
        }
        catch (Exception error) when (error is BackgroundServiceException or InvalidOperationException)
        { WorkerLogSettingsMessage = error.Message; }
        finally { WorkerLogSettingsBusy = false; }
    }

    private async Task<bool> SaveWorkerLogSettingsAsync()
    {
        if (!WorkerLogSettings.IsValidRetention(WorkerLogRetentionDays))
        {
            Message = WorkerLogSettingsMessage = "请填写 1 至 365 的整数天数。";
            return false;
        }
        WorkerLogSettingsBusy = true;
        try
        {
            var settings = await workerLogSettingsGateway!.SaveLogSettingsAsync(
                savedWorkerLogSettings! with { RetentionDays = WorkerLogRetentionDays }, CancellationToken.None);
            ApplyWorkerLogSettings(settings);
            WorkerLogSettingsMessage = $"后台日志已设为保留 {settings.RetentionDays} 天，下次资料清理按此设置执行。";
            return true;
        }
        catch (Exception error) when (error is BackgroundServiceException or InvalidOperationException)
        {
            Message = WorkerLogSettingsMessage = error.Message;
            return false;
        }
        finally { WorkerLogSettingsBusy = false; }
    }

    private void ApplyWorkerLogSettings(WorkerLogSettings? settings)
    {
        savedWorkerLogSettings = settings;
        WorkerLogRetentionDays = settings?.RetentionDays ?? WorkerLogSettings.DefaultRetentionDays;
        OnPropertyChanged(nameof(CanEditWorkerLogSettings));
    }
}
