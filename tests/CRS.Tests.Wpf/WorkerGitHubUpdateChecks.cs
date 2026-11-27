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
        var digest = Convert.ToHexString(SHA256.HashData([12, 23, 42]));
        var release = new WorkerGitHubRelease(new Version(2, 0, 10), "worker-v2.0.10", 3, digest);
        var publication = new WorkerGitHubPublication(new Version(2, 0, 10), "worker-v2.0.10", false, 0);
        var check = new WorkerGitHubCheck(publication, null, release, FromPublicFeed: false);

        using var unusedHttp = new HttpClient(new Handler(_ =>
            throw new InvalidOperationException("发行状态计算不应发起网络请求。")));
        var service = new WorkerUpdateService(new WorkerGitHubReleases(unusedHttp),
            () => new WorkerUpdateEnvironment(true, true, true));

        var unknown = service.Evaluate(check, null);
        var current = service.Evaluate(check, new Version(2, 0, 10));
        var available = service.Evaluate(check, new Version(2, 0, 0));
        if (unknown.State != WorkerUpdateState.InstalledVersionUnknown ||
            current.State != WorkerUpdateState.Current ||
            available.State != WorkerUpdateState.UpdateAvailable || !available.CanInstall)
            throw new InvalidOperationException("后台更新统一状态或安装能力计算错误。");

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

        Console.WriteLine("PASS WorkerUpdateService：统一状态、安装环境门禁、取消和限流异常分类正常。");
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
}
