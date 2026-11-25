using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using CRS.Common.Integrity;

namespace CRS.DesktopClient.Services;

internal sealed record WorkerGitHubRelease(Version Version, string Tag, long Bytes, string Sha256);

internal sealed class WorkerGitHubReleases(HttpClient http)
{
    internal const string Repository = "sword0880/CRSTool";
    internal const string Asset = "CRS.Worker.Service-win-x64.zip";
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const string TagPrefix = "worker-v";
    internal static WorkerGitHubReleases Shared { get; } = new(CreateClient());

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CRS-Worker-Updater/1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal async Task<WorkerGitHubRelease?> CheckAsync(CancellationToken token)
    {
        const int pageSize = 30;
        WorkerGitHubRelease? latest = null;
        for (var page = 1; ; page++)
        {
            var releases = await http.GetFromJsonAsync<Release[]>(
                $"https://api.github.com/repos/{Repository}/releases?per_page={pageSize}&page={page}", token)
                .ConfigureAwait(false) ?? throw new InvalidDataException("GitHub 未返回发行列表。");
            foreach (var release in releases)
            {
                var candidate = ReadRelease(release);
                if (candidate is not null && (latest is null || candidate.Version > latest.Version))
                    latest = candidate;
            }
            if (releases.Length < pageSize) return latest;
        }
    }

    internal async Task DownloadAsync(WorkerGitHubRelease release, string path, CancellationToken token)
    {
        var url = $"https://github.com/{Repository}/releases/download/{release.Tag}/{Asset}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var destination = response.RequestMessage?.RequestUri;
        if (destination is not { Scheme: "https" } ||
            (destination.Host != "github.com" && !destination.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)) ||
            (response.Content.Headers.ContentLength is { } length && length != release.Bytes))
            throw new InvalidDataException("GitHub 安装包地址或大小与发行信息不符。");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > release.Bytes || total > MaxPackageBytes)
                throw new InvalidDataException("GitHub 安装包超出发行大小。");
            hash.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (total != release.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub 安装包下载不完整或校验失败。");
    }

    private static WorkerGitHubRelease? ReadRelease(Release release)
    {
        if (release.Draft || release.Prerelease || release.Tag is not { } tag ||
            !tag.StartsWith(TagPrefix, StringComparison.Ordinal) ||
            !Version.TryParse(tag[TagPrefix.Length..], out var version) || version.Build < 0 || version.Revision >= 0 ||
            tag != TagPrefix + version)
            return null;
        var assets = release.Assets?.Where(asset => asset.Name == Asset).ToArray();
        if (assets is not [var file] || file.State != "uploaded" || file.Size is <= 0 or > MaxPackageBytes ||
            file.Digest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !ContentFingerprint.IsSha256Hex(digest[7..]))
            return null;
        return new(version, tag, file.Size, digest[7..]);
    }

    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, bool Draft, bool Prerelease, ReleaseAsset[]? Assets);
    private sealed record ReleaseAsset(string? Name, string? State, long Size, string? Digest);
}
