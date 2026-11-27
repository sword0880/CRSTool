using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class WorkerGitHubUpdateChecks
{
    public static async Task RunAsync()
    {
        var payload = new byte[] { 12, 23, 42 };
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        var pages = 0;
        var downloads = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com")
            {
                pages++;
                var matches = pages == 1
                    ? Enumerable.Repeat(Release("worker-v2.0.1", digest), 30).ToArray()
                    : [Release("worker-v2.0.10", digest), Release("worker-v3.0.0-preview.1", digest, prerelease: true),
                        Release("worker-v4.0.0", digest, draft: true), Release("worker-v5.0.0", "bad"),
                        Release("v6.0.0", digest), Release("worker-v7.0.0.0", digest)];
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(matches)) };
            }
            if (uri.AbsoluteUri != $"https://github.com/{WorkerGitHubReleases.Repository}/releases/download/worker-v2.0.10/{WorkerGitHubReleases.Asset}")
                throw new InvalidOperationException("安装包请求未绑定到规范化的仓库资产路径。");
            var bytes = ++downloads == 1 ? payload : new byte[] { 1, 2, 3 };
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var client = new WorkerGitHubReleases(http);
        var checkedRelease = await client.CheckAsync(CancellationToken.None);
        var release = checkedRelease.Installable;
        if (pages != 2 || release?.Version != new Version(2, 0, 10) || checkedRelease.Stable?.DisplayVersion != "5.0.0" ||
            checkedRelease.Preview?.DisplayVersion != "3.0.0-preview.1" || checkedRelease.FromPublicFeed)
            throw new InvalidOperationException("正式发行和预发行筛选错误。");
        Console.WriteLine("PASS GitHub 后台更新：跨页排序，预发行可读，只有具备哈希的正式包可安装。");

        var folder = Directory.CreateTempSubdirectory("CRS.WorkerUpdateCheck-");
        var good = Path.Combine(folder.FullName, "good.zip");
        var bad = Path.Combine(folder.FullName, "bad.zip");
        try
        {
            await client.DownloadAsync(release, good, CancellationToken.None);
            if (!File.ReadAllBytes(good).SequenceEqual(payload)) throw new InvalidOperationException("完整下载的内容有误。");
            try
            {
                await client.DownloadAsync(release, bad, CancellationToken.None);
                throw new InvalidOperationException("下载哈希不符时仍被接受。");
            }
            catch (InvalidDataException) { }
            Console.WriteLine("PASS GitHub 后台更新：下载使用固定资产地址，哈希错误被拒绝。");
        }
        finally
        {
            File.Delete(bad);
            File.Delete(good);
            folder.Delete();
        }
        await CheckRateLimitFallbackAsync();
        await CheckUpdateServiceAsync();
    }

    private static async Task CheckUpdateServiceAsync()
    {
        var payload = new byte[] { 12, 23, 42 };
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        var release = new WorkerGitHubRelease(new Version(2, 0, 10), "worker-v2.0.10", payload.Length, digest);
        var publication = new WorkerGitHubPublication(new Version(2, 0, 10), "worker-v2.0.10", false, 0);
        var check = new WorkerGitHubCheck(publication, null, release, FromPublicFeed: false);
        var environment = new WorkerUpdateEnvironment(true, true, true);

        using var unusedHttp = new HttpClient(new Handler(_ =>
            throw new InvalidOperationException("发行状态计算不应发起网络请求。")));
        var service = new WorkerUpdateService(new WorkerGitHubReleases(unusedHttp), () => environment);

        var unknown = service.Evaluate(check, null);
        var current = service.Evaluate(check, new Version(2, 0, 10));
        var available = service.Evaluate(check, new Version(2, 0, 0));
        var feedOnly = service.Evaluate(new WorkerGitHubCheck(publication, null, null, FromPublicFeed: true),
            new Version(2, 0, 0));
        var verified = service.VerifyInstallation(
            new WorkerUpdateResult(WorkerUpdateState.Installed, "待核验", check, release, release.Page),
            release.Version, serviceRunning: true);
        var pending = service.VerifyInstallation(
            new WorkerUpdateResult(WorkerUpdateState.Installed, "待核验", check, release, release.Page),
            new Version(2, 0, 0), serviceRunning: false);
        if (unknown.State != WorkerUpdateState.InstalledVersionUnknown ||
            current.State != WorkerUpdateState.Current ||
            available.State != WorkerUpdateState.UpdateAvailable || !available.CanInstall ||
            feedOnly.State != WorkerUpdateState.ReleaseInfoOnly || feedOnly.CanInstall ||
            verified.State != WorkerUpdateState.InstallationVerified ||
            pending.State != WorkerUpdateState.VerificationPending)
            throw new InvalidOperationException("后台更新统一状态、安装能力或安装后核验计算错误。");

        var unsupported = new WorkerUpdateService(new WorkerGitHubReleases(unusedHttp),
            () => new WorkerUpdateEnvironment(false, true, true)).Evaluate(check, new Version(2, 0, 0));
        var missingPowerShell = new WorkerUpdateService(new WorkerGitHubReleases(unusedHttp),
            () => new WorkerUpdateEnvironment(true, false, true)).Evaluate(check, new Version(2, 0, 0));
        var untrusted = new WorkerUpdateService(new WorkerGitHubReleases(unusedHttp),
            () => new WorkerUpdateEnvironment(true, true, false)).Evaluate(check, new Version(2, 0, 0));
        if (unsupported.State != WorkerUpdateState.UnsupportedEnvironment ||
            missingPowerShell.State != WorkerUpdateState.PowerShellUnavailable ||
            untrusted.State != WorkerUpdateState.PublisherNotTrusted ||
            unsupported.CanInstall || missingPowerShell.CanInstall || untrusted.CanInstall)
            throw new InvalidOperationException("更新环境门禁未阻止不受支持的平台或不可信安装环境。");

        var environmentFailure = new WorkerUpdateService(new WorkerGitHubReleases(unusedHttp),
            () => throw new UnauthorizedAccessException("synthetic environment access failure"));
        var installEnvironmentFailure = await environmentFailure.InstallAsync(
            available, new Version(2, 0, 0), CancellationToken.None);
        if (installEnvironmentFailure.State != WorkerUpdateState.EnvironmentCheckFailed)
            throw new InvalidOperationException("安装环境探测异常未归一化为环境检查失败。");

        var releaseJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = release.Tag, draft = false, prerelease = false,
                assets = new[] { new { name = WorkerGitHubReleases.Asset, state = "uploaded",
                    size = payload.Length, digest = "sha256:" + digest } }
            }
        });
        using var environmentCheckHttp = new HttpClient(new Handler(_ =>
            new(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }));
        var environmentCheck = await new WorkerUpdateService(new WorkerGitHubReleases(environmentCheckHttp),
            () => throw new UnauthorizedAccessException("synthetic environment access failure"))
            .CheckAsync(new Version(2, 0, 0), CancellationToken.None);
        if (environmentCheck.State != WorkerUpdateState.EnvironmentCheckFailed)
            throw new InvalidOperationException("更新检查将本机环境异常错误归类为 GitHub 响应异常。");

        using var viewModelHttp = new HttpClient(new Handler(_ =>
            new(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }));
        var viewModel = new BackgroundServiceViewModel(CancellationToken.None, null, null,
            new WorkerUpdateService(new WorkerGitHubReleases(viewModelHttp), () => environment));
        await viewModel.CheckWorkerUpdatesCommand.ExecuteAsync(null);
        if (viewModel.IsCheckingWorkerUpdates ||
            !viewModel.CheckWorkerUpdatesCommand.CanExecute(null) ||
            viewModel.InstallWorkerUpdateCommand.CanExecute(null) ||
            !viewModel.GitHubUpdateStatus.Contains("当前后台版本未确认", StringComparison.Ordinal))
            throw new InvalidOperationException("后台更新 ViewModel 未正确呈现服务结果或刷新命令状态。");

        using var cancelledHttp = new HttpClient(new Handler(_ =>
            throw new InvalidOperationException("取消的更新检查不应继续发起请求。")));
        using var cancelledToken = new CancellationTokenSource();
        cancelledToken.Cancel();
        var cancelled = await new WorkerUpdateService(new WorkerGitHubReleases(cancelledHttp))
            .CheckAsync(new Version(2, 0, 0), cancelledToken.Token);
        if (cancelled.State != WorkerUpdateState.Cancelled)
            throw new InvalidOperationException("更新检查未正确归一化会话取消状态。");

        using var limitedHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.TooManyRequests)));
        var limited = await new WorkerUpdateService(new WorkerGitHubReleases(limitedHttp))
            .CheckAsync(new Version(2, 0, 0), CancellationToken.None);
        if (limited.State != WorkerUpdateState.RateLimited)
            throw new InvalidOperationException("GitHub 403/429 回退失败后未归类为限流状态。");

        var folder = Directory.CreateTempSubdirectory("CRS.WorkerUpdateService-");
        try
        {
            var installedPath = Path.Combine(folder.FullName, "installed.zip");
            var installCalls = 0;
            using var downloadHttp = new HttpClient(new Handler(_ =>
                new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
            var installService = new WorkerUpdateService(new WorkerGitHubReleases(downloadHttp),
                () => environment,
                (candidate, path) =>
                {
                    installCalls++;
                    if (candidate != release || !File.Exists(path) ||
                        !File.ReadAllBytes(path).SequenceEqual(payload))
                        throw new InvalidOperationException("维护程序未收到完整且经过摘要校验的安装包。");
                    return Task.FromResult(WorkerInstallResult.Completed);
                },
                () => installedPath);
            var installed = await installService.InstallAsync(available, new Version(2, 0, 0), CancellationToken.None);
            if (installed.State != WorkerUpdateState.Installed || installCalls != 1 || File.Exists(installedPath))
                throw new InvalidOperationException("安装协调或成功后的临时安装包清理失败。");

            var rejectedPath = Path.Combine(folder.FullName, "rejected.zip");
            var wrongRelease = release with { Sha256 = new string('0', 64) };
            var wrongAvailability = service.Evaluate(check with { Installable = wrongRelease }, new Version(2, 0, 0));
            var rejectedInstallerCalls = 0;
            using var rejectedHttp = new HttpClient(new Handler(_ =>
                new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
            var rejectedService = new WorkerUpdateService(new WorkerGitHubReleases(rejectedHttp),
                () => environment,
                (_, _) => { rejectedInstallerCalls++; return Task.FromResult(WorkerInstallResult.Completed); },
                () => rejectedPath);
            var rejected = await rejectedService.InstallAsync(wrongAvailability, new Version(2, 0, 0), CancellationToken.None);
            if (rejected.State != WorkerUpdateState.PackageRejected || rejectedInstallerCalls != 0 || File.Exists(rejectedPath))
                throw new InvalidOperationException("摘要校验失败后仍安装或未清理临时包。");

            var cancelledPath = Path.Combine(folder.FullName, "cancelled.zip");
            using var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            using var cancelledDownloadHttp = new HttpClient(new AsyncHandler((_, token) =>
                Task.FromCanceled<HttpResponseMessage>(token)));
            var cancelledInstall = await new WorkerUpdateService(new WorkerGitHubReleases(cancelledDownloadHttp),
                () => environment,
                (_, _) => throw new InvalidOperationException("取消下载后不应启动维护程序。"),
                () => cancelledPath)
                .InstallAsync(available, new Version(2, 0, 0), alreadyCancelled.Token);
            if (cancelledInstall.State != WorkerUpdateState.Cancelled || File.Exists(cancelledPath))
                throw new InvalidOperationException("用户会话取消未终止下载或未清理临时包。");

            var timeoutPath = Path.Combine(folder.FullName, "timeout.zip");
            using var slowHttp = new HttpClient(new AsyncHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            var timedOut = await new WorkerUpdateService(new WorkerGitHubReleases(slowHttp),
                () => environment,
                (_, _) => throw new InvalidOperationException("下载超时后不应启动维护程序。"),
                () => timeoutPath,
                TimeSpan.FromMilliseconds(50))
                .InstallAsync(available, new Version(2, 0, 0), CancellationToken.None);
            if (timedOut.State != WorkerUpdateState.DownloadTimedOut || File.Exists(timeoutPath))
                throw new InvalidOperationException("下载超时未正确归类或未清理临时安装包。");

            var cancelledByInstallerPath = Path.Combine(folder.FullName, "installer-cancelled.zip");
            using var cancelledByInstallerHttp = new HttpClient(new Handler(_ =>
                new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
            var cancelledByInstaller = await new WorkerUpdateService(new WorkerGitHubReleases(cancelledByInstallerHttp),
                () => environment,
                (_, path) =>
                {
                    if (!File.Exists(path)) throw new InvalidOperationException("提权前安装包已丢失。");
                    return Task.FromResult(WorkerInstallResult.Cancelled);
                },
                () => cancelledByInstallerPath)
                .InstallAsync(available, new Version(2, 0, 0), CancellationToken.None);
            if (cancelledByInstaller.State != WorkerUpdateState.InstallCancelled ||
                !cancelledByInstaller.CanInstall || File.Exists(cancelledByInstallerPath))
                throw new InvalidOperationException("用户取消提权后应允许重新尝试，并清理安装包。");
        }
        finally
        {
            folder.Delete(true);
        }

        Console.WriteLine("PASS WorkerUpdateService：状态、环境门禁、限流、取消、超时、摘要校验、安装协调及临时包清理正常。");
    }

    private static async Task CheckRateLimitFallbackAsync()
    {
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link href="https://github.com/sword0880/CRSTool/releases/tag/worker-v2.0.0-preview.1" /></entry>
              <entry><link href="https://github.com/sword0880/CRSTool/releases/tag/worker-v2.0.0-preview.10" /></entry>
              <entry><link href="https://github.com/sword0880/CRSTool/releases/tag/worker-v2.0.1" /></entry>
              <entry><link href="https://example.com/sword0880/CRSTool/releases/tag/worker-v9.0.0" /></entry>
              <entry><link href="https://github.com/other/CRSTool/releases/tag/worker-v9.0.0" /></entry>
              <entry><link href="https://github.com/sword0880/CRSTool/releases/tag/worker-v9.0.0-preview.01" /></entry>
            </feed>
            """;
        foreach (var rateLimit in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            using var http = new HttpClient(new Handler(request =>
            {
                if (request.RequestUri?.Host == "api.github.com") return new(rateLimit);
                if (request.RequestUri?.AbsoluteUri != WorkerGitHubReleases.ReleasesPage + ".atom")
                    throw new InvalidOperationException("未使用本仓库的公开发行订阅。");
                return new(HttpStatusCode.OK) { Content = new StringContent(feed) };
            }));
            var result = await new WorkerGitHubReleases(http).CheckAsync(CancellationToken.None);
            if (!result.FromPublicFeed || result.Installable is not null || result.Stable?.DisplayVersion != "2.0.1" ||
                result.Preview?.DisplayVersion != "2.0.0-preview.10" ||
                result.Preview.Page.Host != "github.com")
                throw new InvalidOperationException("公开发行订阅须显示本仓库发行，且不能启用在线安装。");
        }
        Console.WriteLine("PASS GitHub 后台更新：403/429 后读取 Atom，正确识别预发行排序，并拒绝把 Atom 条目用作安装授权。");
    }

    internal static async Task RunLiveAsync(string previewVersion)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var model = new BackgroundServiceViewModel(deadline.Token);
        await model.CheckWorkerUpdatesCommand.ExecuteAsync(null);
        Console.WriteLine(model.GitHubUpdateStatus);
        if (!model.GitHubUpdateStatus.Contains(previewVersion, StringComparison.Ordinal) ||
            !model.GitHubUpdateStatus.Contains("手动安装", StringComparison.Ordinal) ||
            model.InstallWorkerUpdateCommand.CanExecute(null))
            throw new InvalidOperationException("前台未能从实际 GitHub 读取并正确呈现预发行版。");
    }

    private static object Release(string tag, string digest, bool draft = false, bool prerelease = false) => new
    {
        tag_name = tag, draft, prerelease, assets = new[]
        {
            new { name = WorkerGitHubReleases.Asset, state = "uploaded", size = 3, digest = "sha256:" + digest }
        }
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class AsyncHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await respond(request, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }
}
