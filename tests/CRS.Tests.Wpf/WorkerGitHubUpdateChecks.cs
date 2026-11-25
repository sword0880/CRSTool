using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.DesktopClient.Services;

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
                    : [Release("worker-v2.0.10", digest), Release("worker-v3.0.0", digest, prerelease: true),
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
        var release = await client.CheckAsync(CancellationToken.None);
        if (pages != 2 || release?.Version != new Version(2, 0, 10)) throw new InvalidOperationException("发行筛选或版本排序错误。");
        Console.WriteLine("PASS GitHub 后台更新：跨页检查，只选择带哈希的正式 Worker 包并按数值比较。");

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
