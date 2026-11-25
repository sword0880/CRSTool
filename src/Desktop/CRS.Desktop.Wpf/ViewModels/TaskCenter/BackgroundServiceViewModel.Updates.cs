using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.DesktopClient.Services;

namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    private WorkerGitHubCheck? githubCheck;
    private Version? installedWorkerVersion;
    private DateTimeOffset? lastGitHubCheck;
    private Uri githubReleasePage = new(WorkerGitHubReleases.ReleasesPage);
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            githubCheck = await WorkerGitHubReleases.Shared.CheckAsync(deadline.Token);
            githubReleasePage = githubCheck.Installable?.Page ?? (githubCheck.Stable ?? githubCheck.Preview)?.Page
                ?? new(WorkerGitHubReleases.ReleasesPage);
            GitHubUpdateStatus = DescribeGitHubRelease();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException or System.Xml.XmlException or OperationCanceledException)
        {
            githubCheck = null;
            GitHubUpdateStatus = error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }
                ? "GitHub 暂时限制发行查询。可打开发布页查看。"
                : "暂时无法检查 GitHub 发行。可打开发布页查看。";
        }
        finally { IsCheckingWorkerUpdates = false; }
    }

    private bool CanInstallWorkerUpdate() =>
        !IsCheckingWorkerUpdates && !IsInstallingWorkerUpdate && !lifetime.IsCancellationRequested &&
        githubCheck?.Installable is { } release && installedWorkerVersion is { } installed && release.Version > installed &&
        WorkerReleaseInstaller.SupportsPackage && WorkerReleaseInstaller.HasTrustedPublisher();

    private string DescribeGitHubRelease()
    {
        if (githubCheck?.Installable is not { } release)
        {
            if (githubCheck?.Stable is { } stable)
                return githubCheck.FromPublicFeed
                    ? $"已读取 GitHub 后台 {stable.DisplayVersion} 的发布记录。API 暂时限流，安装包校验信息待确认。"
                    : $"已找到后台 {stable.DisplayVersion}。安装包或校验信息尚未齐备。";
            if (githubCheck?.Preview is { } preview)
                return $"已找到后台 {preview.DisplayVersion} 开发预发行版。请在发布页下载并手动安装。";
            return githubCheck?.FromPublicFeed == true
                ? "GitHub 公开发行订阅里暂未找到后台版本。"
                : "GitHub 暂无可用的正式后台发行包。";
        }
        if (installedWorkerVersion is not { } installed)
            return $"GitHub 发布了 {release.Version}。当前后台版本未确认。";
        if (release.Version <= installed) return $"后台已达到 GitHub 可安装版本 {release.Version}。";
        if (!WorkerReleaseInstaller.SupportsPackage) return $"发现后台更新 {release.Version}。当前发行支持 Windows x64。";
        return WorkerReleaseInstaller.HasTrustedPublisher()
            ? $"发现后台更新 {release.Version}，可安装。"
            : $"发现后台更新 {release.Version}。请由管理员配置受信发布者后安装。";
    }

    private void SetInstalledWorkerVersion(string? version)
    {
        installedWorkerVersion = Version.TryParse(version, out var parsed) ? parsed : null;
        if (githubCheck is not null && !IsCheckingWorkerUpdates && !IsInstallingWorkerUpdate)
            GitHubUpdateStatus = DescribeGitHubRelease();
        InstallWorkerUpdateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanInstallWorkerUpdate))]
    private async Task InstallWorkerUpdateAsync()
    {
        if (!CanInstallWorkerUpdate() || githubCheck?.Installable is not { } release) return;
        IsInstallingWorkerUpdate = true;
        string? packagePath = null;
        try
        {
            await ReadServiceHealthAsync();
            lifetime.ThrowIfCancellationRequested();
            if (installedWorkerVersion is not { } installed || release.Version <= installed)
            {
                GitHubUpdateStatus = DescribeGitHubRelease();
                return;
            }
            if (!File.Exists(WorkerReleaseInstaller.PowerShellPath))
            {
                GitHubUpdateStatus = "安装后台更新需要 PowerShell 7 x64。";
                return;
            }
            GitHubUpdateStatus = $"正在下载后台 {release.Version}……";
            packagePath = Path.Combine(Path.GetTempPath(), $"CRS-Worker-{Guid.NewGuid():N}.zip");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await WorkerGitHubReleases.Shared.DownloadAsync(release, packagePath, deadline.Token);
            lifetime.ThrowIfCancellationRequested();
            GitHubUpdateStatus = "正在校验并安装。升级期间后台任务会暂时停止。";
            var result = await WorkerReleaseInstaller.InstallAsync(release, packagePath);
            if (lifetime.IsCancellationRequested) return;
            var response = await ReadServiceHealthAsync();
            GitHubUpdateStatus = result switch
            {
                WorkerInstallResult.Completed when installedWorkerVersion == release.Version && response?.Health?.State == "Running"
                    => $"后台 {release.Version} 已安装，运行正常。",
                WorkerInstallResult.Completed => "维护程序已结束，当前运行版本尚未核实。请刷新服务状态。",
                WorkerInstallResult.Cancelled => "已取消安装。",
                WorkerInstallResult.PowerShellUnavailable => "安装后台更新需要 PowerShell 7 x64。",
                WorkerInstallResult.PackageRejected => "安装包校验未通过。请核对发布者签名、版本和桌面兼容性。",
                _ => "后台升级未完成。请查看当前服务状态和维护记录。"
            };
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            GitHubUpdateStatus = "下载或启动后台安装失败。请检查网络、权限或本机安装环境后重试。";
        }
        finally
        {
            if (packagePath is not null)
            {
                try { File.Delete(packagePath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            IsInstallingWorkerUpdate = false;
        }
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
