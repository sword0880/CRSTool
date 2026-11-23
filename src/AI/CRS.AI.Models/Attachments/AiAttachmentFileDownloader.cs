using System.Net;
using System.Security.Cryptography;

namespace CRS.AI.Models.Attachments;

public sealed record AiAttachmentFileInput(string DownloadUrl, string FileId, string? MimeType, string? FileName);

public interface IAiAttachmentFileDownloader
{
    Task<byte[]> DownloadAsync(AiAttachmentFileInput file, int maximumBytes, CancellationToken token);
}

/// <summary>仅从 ChatGPT 官方文件域下载短期文件；不跟随重定向、不访问任意 URL、不落盘。</summary>
public sealed class OpenAiAttachmentFileDownloader : IAiAttachmentFileDownloader, IDisposable
{
    private static readonly string[] AllowedHosts = ["files.oaiusercontent.com", "chatgpt.com"];
    private readonly HttpClient http;
    private readonly bool ownsClient;

    public OpenAiAttachmentFileDownloader(HttpClient? httpClient = null)
    {
        ownsClient = httpClient is null;
        http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        }) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<byte[]> DownloadAsync(AiAttachmentFileInput file, int maximumBytes, CancellationToken token)
    {
        ValidateFile(file, maximumBytes);
        if (!Uri.TryCreate(file.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || IPAddress.TryParse(uri.Host, out _) || !IsAllowedHost(uri.Host))
            throw new InvalidDataException("附件下载地址不受信任。");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
            HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("附件下载地址不允许重定向。");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("无法从 ChatGPT 文件服务读取附件。");
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("附件不能超过 10 MiB。");

        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (output.Length + read > maximumBytes)
                    throw new InvalidDataException("附件不能超过 10 MiB。");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (output.Length == 0) throw new InvalidDataException("附件内容为空。");
            var result = output.ToArray();
            CryptographicOperations.ZeroMemory(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment))
                CryptographicOperations.ZeroMemory(segment.AsSpan(0, checked((int)output.Length)));
        }
    }

    public void Dispose()
    {
        if (ownsClient) http.Dispose();
    }

    private static void ValidateFile(AiAttachmentFileInput file, int maximumBytes)
    {
        if (maximumBytes is < 1 or > AiAttachmentParser.MaxFileBytes || file is null || file.FileId is not { Length: > 0 and <= 128 } ||
            file.FileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) ||
            file.DownloadUrl is not { Length: > 0 and <= 8192 })
            throw new InvalidDataException("ChatGPT 附件参数无效。");
    }

    private static bool IsAllowedHost(string host)
    {
        var normalized = host.TrimEnd('.').ToLowerInvariant();
        return AllowedHosts.Contains(normalized, StringComparer.Ordinal) ||
            normalized.EndsWith(".oaiusercontent.com", StringComparison.Ordinal) ||
            normalized.EndsWith(".chatgpt.com", StringComparison.Ordinal);
    }
}
