using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using CRS.Common.Integrity;

namespace CRS.DesktopClient.Services;

internal sealed record WorkerGitHubRelease(Version Version, string Tag, long Bytes, string Sha256)
{
    internal Uri Page => new($"{WorkerGitHubReleases.ReleasesPage}/tag/{Tag}");
}

internal sealed record WorkerGitHubPublication(Version Version, string Tag, bool IsPreview, int PreviewNumber)
{
    internal string DisplayVersion => Tag["worker-v".Length..];
    internal Uri Page => new($"{WorkerGitHubReleases.ReleasesPage}/tag/{Tag}");
}

internal sealed record WorkerGitHubCheck(WorkerGitHubPublication? Stable, WorkerGitHubPublication? Preview,
    WorkerGitHubRelease? Installable, bool FromPublicFeed);

internal sealed class WorkerGitHubReleases(HttpClient http)
{
    internal const string Repository = "sword0880/CRSTool";
    internal const string ReleasesPage = $"https://github.com/{Repository}/releases";
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

    internal async Task<WorkerGitHubCheck> CheckAsync(CancellationToken token)
    {
        try
        {
            return await CheckApiAsync(token).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            return await CheckPublicFeedAsync(token).ConfigureAwait(false);
        }
    }

    private async Task<WorkerGitHubCheck> CheckApiAsync(CancellationToken token)
    {
        const int pageSize = 30;
        WorkerGitHubPublication? stable = null;
        WorkerGitHubPublication? preview = null;
        WorkerGitHubRelease? latest = null;
        for (var page = 1; ; page++)
        {
            var releases = await http.GetFromJsonAsync<Release[]>(
                $"https://api.github.com/repos/{Repository}/releases?per_page={pageSize}&page={page}", token)
                .ConfigureAwait(false) ?? throw new InvalidDataException("GitHub 未返回发行列表。");
            foreach (var release in releases)
            {
                if (release.Draft || ReadPublication(release.Tag, release.Prerelease) is not { } publication) continue;
                if (publication.IsPreview)
                {
                    if (IsNewer(publication, preview)) preview = publication;
                }
                else
                {
                    if (IsNewer(publication, stable)) stable = publication;
                    var candidate = ReadInstallableRelease(publication, release.Assets);
                    if (candidate is not null && (latest is null || candidate.Version > latest.Version))
                        latest = candidate;
                }
            }
            if (releases.Length < pageSize) return new(stable, preview, latest, FromPublicFeed: false);
        }
    }

    private async Task<WorkerGitHubCheck> CheckPublicFeedAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesPage + ".atom");
        request.Headers.Accept.ParseAdd("application/atom+xml");
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var text = new StringReader(content);
        using var xml = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
        });
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var feed = XDocument.Load(xml).Root;
        if (feed is null || feed.Name != atom + "feed") throw new InvalidDataException("GitHub 未返回有效的公开发行订阅。");

        WorkerGitHubPublication? stable = null;
        WorkerGitHubPublication? preview = null;
        const string tagPath = $"/{Repository}/releases/tag/";
        foreach (var entry in feed.Elements(atom + "entry"))
        {
            foreach (var link in entry.Elements(atom + "link"))
            {
                if (!Uri.TryCreate((string?)link.Attribute("href"), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort ||
                    !uri.AbsolutePath.StartsWith(tagPath, StringComparison.Ordinal) ||
                    ReadPublication(uri.AbsolutePath[tagPath.Length..]) is not { } publication)
                    continue;

                if (publication.IsPreview)
                {
                    if (IsNewer(publication, preview)) preview = publication;
                }
                else if (IsNewer(publication, stable)) stable = publication;
            }
        }
        // Atom 不提供资产哈希或 GitHub 的预发行标志，任何条目都只用于显示发布信息。
        return new(stable, preview, Installable: null, FromPublicFeed: true);
    }

    internal async Task DownloadAsync(WorkerGitHubRelease release, string path, CancellationToken token)
    {
        var url = $"{ReleasesPage}/download/{release.Tag}/{Asset}";
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

    private static WorkerGitHubPublication? ReadPublication(string? tag, bool prerelease = false)
    {
        if (tag is null || !tag.StartsWith(TagPrefix, StringComparison.Ordinal)) return null;
        const string previewPrefix = "-preview.";
        var suffix = tag.IndexOf(previewPrefix, StringComparison.Ordinal);
        var versionText = suffix < 0 ? tag[TagPrefix.Length..] : tag[TagPrefix.Length..suffix];
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || version.Revision >= 0 ||
            versionText != version.ToString()) return null;
        if (suffix < 0) return new(version, tag, prerelease, PreviewNumber: 0);
        var number = tag[(suffix + previewPrefix.Length)..];
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var previewNumber) &&
            previewNumber > 0 && number == previewNumber.ToString(CultureInfo.InvariantCulture)
            ? new(version, tag, IsPreview: true, previewNumber) : null;
    }

    private static bool IsNewer(WorkerGitHubPublication publication, WorkerGitHubPublication? current) =>
        current is null || publication.Version > current.Version ||
        publication.Version == current.Version && publication.PreviewNumber > current.PreviewNumber;

    private static WorkerGitHubRelease? ReadInstallableRelease(WorkerGitHubPublication release, ReleaseAsset[]? releaseAssets)
    {
        var assets = releaseAssets?.Where(asset => asset.Name == Asset).ToArray();
        if (assets is not [var file] || file.State != "uploaded" || file.Size is <= 0 or > MaxPackageBytes ||
            file.Digest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !ContentFingerprint.IsSha256Hex(digest[7..]))
            return null;
        return new(release.Version, release.Tag, file.Size, digest[7..]);
    }

    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, bool Draft, bool Prerelease, ReleaseAsset[]? Assets);
    private sealed record ReleaseAsset(string? Name, string? State, long Size, string? Digest);
}
