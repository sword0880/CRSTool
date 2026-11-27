using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.DesktopClient.Services;

namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    private readonly WorkerUpdateService updateService = new();
    private WorkerUpdateResult? updateResult;
    private Version? installedWorkerVersion;
    private DateTimeOffset? lastGitHubCheck;
    private Uri githubReleasePage = WorkerUpdateService.DefaultReleasePage;

    [ObservableProperty] private string gitHubUpdateStatus = "尚未检查 GitHub 发行。";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallWorkerUpdateCommand))]
    private bool isCheckingWorkerUpdates;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckWorkerUpdatesCommand)),
        NotifyCanExecuteChangedFor(nameof(InstallWorkerUpdateCommand))]
    private bool isInstallingWorkerUpdate;

    internal async Task OpenServiceStatusAsync()
    {
        await ReadServiceHealthAsync();
        if ((lastGitHubCheck is null || DateTimeOffset.UtcNow - lastGitHubCheck >= TimeSpan.FromMinutes(15)) &&
            CheckWorkerUpdatesCommand.CanExecute(null))
            await CheckWorkerUpdatesCommand.ExecuteAsync(null);
    }

    private bool CanCheckWorkerUpdates() => !IsInstallingWorkerUpdate && !lifetime.IsCancellationRequested;

    [RelayCommand(CanExecute = nameof(CanCheckWorkerUpdates))]
    private async Task CheckWorkerUpdatesAsync()
    {
        if (!CanCheckWorkerUpdates()) return;
        IsCheckingWorkerUpdates = true;
        lastGitHubCheck = DateTimeOffset.UtcNow;
        GitHubUpdateStatus = "正在检查 GitHub 发行……";
        try
        {
            var result = await updateService.CheckAsync(installedWorkerVersion, lifetime);
            if (result.State != WorkerUpdateState.Cancelled)
                ApplyUpdateResult(result);
        }
        finally
        {
            IsCheckingWorkerUpdates = false;
        }
    }

    private bool CanInstallWorkerUpdate() =>
        !IsCheckingWorkerUpdates && !IsInstallingWorkerUpdate && !lifetime.IsCancellationRequested &&
        updateResult?.CanInstall == true;

    private void SetInstalledWorkerVersion(string? version)
    {
        installedWorkerVersion = Version.TryParse(version, out var parsed) ? parsed : null;
        if (updateResult?.Check is { } check && !IsCheckingWorkerUpdates && !IsInstallingWorkerUpdate)
            ApplyUpdateResult(updateService.Evaluate(check, installedWorkerVersion));
        else
            InstallWorkerUpdateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanInstallWorkerUpdate))]
    private async Task InstallWorkerUpdateAsync()
    {
        var availability = updateResult;
        if (!CanInstallWorkerUpdate() || availability?.Check is not { } check) return;
        IsInstallingWorkerUpdate = true;
        try
        {
            // 安装前重新读取后台版本，避免使用过期的可安装状态。
            await ReadServiceHealthAsync();
            lifetime.ThrowIfCancellationRequested();

            var readiness = updateService.Evaluate(check, installedWorkerVersion);
            ApplyUpdateResult(readiness);
            if (!readiness.CanInstall) return;

            var progress = new Progress<WorkerUpdateResult>(ApplyUpdateResult);
            var result = await updateService.InstallAsync(readiness, installedWorkerVersion, lifetime, progress);
            ApplyUpdateResult(result);

            if (result.State == WorkerUpdateState.Installed)
            {
                // 安装程序退出不等于服务已成功启动，必须重新读取健康快照确认。
                var response = await ReadServiceHealthAsync();
                ApplyUpdateResult(updateService.VerifyInstallation(
                    result, installedWorkerVersion, response?.Health?.State == "Running"));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            IsInstallingWorkerUpdate = false;
        }
    }

    private void ApplyUpdateResult(WorkerUpdateResult result)
    {
        updateResult = result;
        githubReleasePage = result.ReleasePage ?? WorkerUpdateService.DefaultReleasePage;
        if (result.State != WorkerUpdateState.Cancelled && !string.IsNullOrWhiteSpace(result.Message))
            GitHubUpdateStatus = result.Message;
        InstallWorkerUpdateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenGitHubReleasePage()
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(githubReleasePage.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            GitHubUpdateStatus = "无法打开 GitHub 发布页，请检查默认浏览器设置。";
        }
    }
}
