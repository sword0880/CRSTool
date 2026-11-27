using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Xml;

namespace CRS.DesktopClient.Services;

/// <summary>后台更新统一状态；界面只消费结果，不自行解释网络、安装环境或文件生命周期。</summary>
internal enum WorkerUpdateState
{
    NoRelease,
    ReleaseInfoOnly,
    Current,
    InstalledVersionUnknown,
    UpdateAvailable,
    RateLimited,
    CheckUnavailable,
    InvalidResponse,
    TimedOut,
    Cancelled,
    UnsupportedEnvironment,
    PowerShellUnavailable,
    PublisherNotTrusted,
    Downloading,
    Installing,
    Installed,
    InstallationVerified,
    VerificationPending,
    InstallCancelled,
    DownloadTimedOut,
    DownloadFailed,
    PackageRejected,
    InstallFailed
}

/// <summary>更新检查和安装共用的结果模型，保留发行信息供界面打开发布页及刷新安装能力。</summary>
internal sealed record WorkerUpdateResult(
    WorkerUpdateState State,
    string Message,
    WorkerGitHubCheck? Check = null,
    WorkerGitHubRelease? Release = null,
    Uri? ReleasePage = null,
    bool CanInstall = false);

/// <summary>更新环境快照；单元测试可注入确定性环境，生产环境始终读取本机真实状态。</summary>
internal sealed record WorkerUpdateEnvironment(bool SupportsPackage, bool HasPowerShell, bool HasTrustedPublisher);

/// <summary>
/// 统一协调后台更新检查、异常分类、安装环境校验、下载与安装。
/// WorkerGitHubReleases 只负责 GitHub 数据传输，WorkerReleaseInstaller 只负责本机维护程序。
/// </summary>
internal sealed class WorkerUpdateService
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private readonly WorkerGitHubReleases github;
    private readonly Func<WorkerUpdateEnvironment> readEnvironment;

    internal WorkerUpdateService()
        : this(WorkerGitHubReleases.Shared, ReadEnvironment)
    {
    }

    internal WorkerUpdateService(
        WorkerGitHubReleases github,
        Func<WorkerUpdateEnvironment>? readEnvironment = null)
    {
        this.github = github;
        this.readEnvironment = readEnvironment ?? ReadEnvironment;
    }

    /// <summary>检查 GitHub 发行并将异常归一化为界面可直接展示的状态。</summary>
    internal async Task<WorkerUpdateResult> CheckAsync(Version? installedVersion, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CheckTimeout);
        try
        {
            var check = await github.CheckAsync(deadline.Token).ConfigureAwait(false);
            return Evaluate(check, installedVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(WorkerUpdateState.Cancelled, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new(WorkerUpdateState.TimedOut, "GitHub 更新检查超时。请稍后重试或打开发布页查看。");
        }
        catch (HttpRequestException error) when (IsRateLimit(error))
        {
            return new(WorkerUpdateState.RateLimited, "GitHub 暂时限制发行查询。可打开发布页查看。");
        }
        catch (InvalidDataException)
        {
            return new(WorkerUpdateState.InvalidResponse, "GitHub 返回的发行信息无法识别。可打开发布页查看。");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return new(WorkerUpdateState.CheckUnavailable, "暂时无法连接 GitHub 检查发行。可打开发布页查看。");
        }
        catch (Exception error) when (error is JsonException or XmlException or InvalidOperationException)
        {
            return new(WorkerUpdateState.InvalidResponse, "GitHub 返回的发行信息无法识别。可打开发布页查看。");
        }
    }

    /// <summary>根据已读取的发行信息和后台版本重新计算是否允许安装。</summary>
    internal WorkerUpdateResult Evaluate(WorkerGitHubCheck check, Version? installedVersion)
    {
        var releasePage = check.Installable?.Page
            ?? (check.Stable ?? check.Preview)?.Page
            ?? new Uri(WorkerGitHubReleases.ReleasesPage);

        if (check.Installable is not { } release)
        {
            if (check.Stable is { } stable)
                return new(WorkerUpdateState.ReleaseInfoOnly,
                    check.FromPublicFeed
                        ? $"已读取 GitHub 后台 {stable.DisplayVersion} 的发布记录。API 暂时限流，安装包校验信息待确认。"
                        : $"已找到后台 {stable.DisplayVersion}。安装包或校验信息尚未齐备。",
                    check, ReleasePage: releasePage);
            if (check.Preview is { } preview)
                return new(WorkerUpdateState.ReleaseInfoOnly,
                    $"已找到后台 {preview.DisplayVersion} 开发预发行版。请在发布页下载并手动安装。",
                    check, ReleasePage: releasePage);
            return new(WorkerUpdateState.NoRelease,
                check.FromPublicFeed
                    ? "GitHub 公开发行订阅里暂未找到后台版本。"
                    : "GitHub 暂无可用的正式后台发行包。",
                check, ReleasePage: releasePage);
        }

        if (installedVersion is null)
            return new(WorkerUpdateState.InstalledVersionUnknown,
                $"GitHub 发布了 {release.Version}。当前后台版本未确认。",
                check, release, releasePage);

        if (release.Version <= installedVersion)
            return new(WorkerUpdateState.Current,
                $"后台已达到 GitHub 可安装版本 {release.Version}。",
                check, release, releasePage);

        var environment = readEnvironment();
        if (!environment.SupportsPackage)
            return new(WorkerUpdateState.UnsupportedEnvironment,
                $"发现后台更新 {release.Version}。当前发行支持 Windows x64。",
                check, release, releasePage);
        if (!environment.HasPowerShell)
            return new(WorkerUpdateState.PowerShellUnavailable,
                "安装后台更新需要 PowerShell 7 x64。",
                check, release, releasePage);
        if (!environment.HasTrustedPublisher)
            return new(WorkerUpdateState.PublisherNotTrusted,
                $"发现后台更新 {release.Version}。请由管理员配置受信发布者后安装。",
                check, release, releasePage);

        return new(WorkerUpdateState.UpdateAvailable,
            $"发现后台更新 {release.Version}，可安装。",
            check, release, releasePage, CanInstall: true);
    }

    /// <summary>下载并协调后台安装；临时安装包由本服务创建并在所有退出路径中清理。</summary>
    internal async Task<WorkerUpdateResult> InstallAsync(
        WorkerUpdateResult availability,
        Version? installedVersion,
        CancellationToken cancellationToken,
        IProgress<WorkerUpdateResult>? progress = null)
    {
        if (availability.Check is not { } check || availability.Release is not { } release)
            return new(WorkerUpdateState.NoRelease, "没有可安装的后台发行包。", availability.Check,
                ReleasePage: availability.ReleasePage);

        var readiness = Evaluate(check, installedVersion);
        if (!readiness.CanInstall) return readiness;

        string? packagePath = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(DownloadTimeout);

            packagePath = Path.Combine(Path.GetTempPath(), $"CRS-Worker-{Guid.NewGuid():N}.zip");
            progress?.Report(new(WorkerUpdateState.Downloading, $"正在下载后台 {release.Version}……",
                check, release, release.Page));
            await github.DownloadAsync(release, packagePath, deadline.Token).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(WorkerUpdateState.Installing, "正在校验并安装。升级期间后台任务会暂时停止。",
                check, release, release.Page));

            // 提权维护进程启动后必须完成事务，不因桌面会话取消而中断服务升级。
            var installResult = await WorkerReleaseInstaller.InstallAsync(release, packagePath).ConfigureAwait(false);
            return installResult switch
            {
                WorkerInstallResult.Completed => new(WorkerUpdateState.Installed,
                    "维护程序已结束，正在核对后台状态。", check, release, release.Page),
                WorkerInstallResult.Cancelled => new(WorkerUpdateState.InstallCancelled,
                    "已取消安装。", check, release, release.Page),
                WorkerInstallResult.PowerShellUnavailable => new(WorkerUpdateState.PowerShellUnavailable,
                    "安装后台更新需要 PowerShell 7 x64。", check, release, release.Page),
                WorkerInstallResult.PackageRejected => new(WorkerUpdateState.PackageRejected,
                    "安装包校验未通过。请核对发布者签名、版本和桌面兼容性。", check, release, release.Page),
                _ => new(WorkerUpdateState.InstallFailed,
                    "后台升级未完成。请查看当前服务状态和维护记录。", check, release, release.Page)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(WorkerUpdateState.Cancelled, string.Empty, check, release, release.Page);
        }
        catch (OperationCanceledException)
        {
            return new(WorkerUpdateState.DownloadTimedOut, "后台更新下载超时。请检查网络后重试。",
                check, release, release.Page);
        }
        catch (HttpRequestException error) when (IsRateLimit(error))
        {
            return new(WorkerUpdateState.RateLimited, "GitHub 暂时限制安装包下载。请稍后重试。",
                check, release, release.Page);
        }
        catch (InvalidDataException)
        {
            return new(WorkerUpdateState.PackageRejected, "安装包下载不完整或校验失败，已拒绝安装。",
                check, release, release.Page);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return new(WorkerUpdateState.DownloadFailed, "后台更新下载失败。请检查网络和临时目录权限后重试。",
                check, release, release.Page);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new(WorkerUpdateState.InstallFailed, "后台安装程序无法启动。请检查管理员权限和本机安装环境。",
                check, release, release.Page);
        }
        finally
        {
            DeleteTemporaryPackage(packagePath);
        }
    }

    /// <summary>安装程序结束后，以后台健康快照确认版本和运行状态。</summary>
    internal WorkerUpdateResult VerifyInstallation(
        WorkerUpdateResult installation,
        Version? installedVersion,
        bool serviceRunning)
    {
        if (installation.State != WorkerUpdateState.Installed || installation.Release is not { } release)
            return installation;

        return installedVersion == release.Version && serviceRunning
            ? installation with
            {
                State = WorkerUpdateState.InstallationVerified,
                Message = $"后台 {release.Version} 已安装，运行正常。"
            }
            : installation with
            {
                State = WorkerUpdateState.VerificationPending,
                Message = "维护程序已结束，当前运行版本尚未核实。请刷新服务状态。"
            };
    }

    private static WorkerUpdateEnvironment ReadEnvironment() => new(
        WorkerReleaseInstaller.SupportsPackage,
        File.Exists(WorkerReleaseInstaller.PowerShellPath),
        WorkerReleaseInstaller.HasTrustedPublisher());

    private static bool IsRateLimit(HttpRequestException error) =>
        error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    private static void DeleteTemporaryPackage(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 临时包清理失败不覆盖安装结果；下次启动可由系统临时目录清理策略回收。
        }
    }
}
