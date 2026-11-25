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
