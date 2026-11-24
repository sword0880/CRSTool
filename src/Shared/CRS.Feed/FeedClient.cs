using System.Net;
using System.Net.Http.Headers;
using CRS.Common.Net;

namespace CRS.Feed;

/// <summary>负责下载 Feed、处理条件请求、重定向、限流和正文大小上限。</summary>
public sealed class FeedClient : IDisposable
{
    private readonly HttpClient http;
    private readonly FeedClientOptions options;

    public FeedClient(HttpMessageHandler? handler = null, FeedClientOptions? options = null)
    {
        this.options = options ?? new FeedClientOptions();
        this.options.Validate();
        if (handler is not null && (this.options.ProxyAddress is not null || this.options.Proxy is not null))
            throw new ArgumentException("自定义 Feed transport 需自行负责代理。", nameof(handler));

        // 默认关闭自动重定向，由本类逐跳校验目标地址。
        var transport = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            Proxy = this.options.Proxy ?? (this.options.ProxyAddress is { } address ? new WebProxy(address) : null),
            UseProxy = this.options.Proxy is not null || this.options.ProxyAddress is not null || this.options.UseSystemProxy
        };
        http = new HttpClient(transport, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<FeedFetchResult> FetchAsync(
        Uri address, FeedValidators? validators = null, CancellationToken cancellationToken = default)
    {
        var original = FeedAddress.Validate(address, options.AllowHttp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);

        var current = original;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var redirect = 0; ; redirect++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!visited.Add(current.AbsoluteUri))
                    return new(FeedFetchStatus.RedirectRejected);

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/feed+json, application/json, application/xml, text/xml;q=0.9, */*;q=0.5");
                request.Headers.UserAgent.ParseAdd(options.UserAgent);

                // 条件请求只发送给最初的 Feed 地址，避免把缓存验证器泄漏给跨站重定向目标。
                if (redirect == 0 && validators?.ETag is { Length: > 0 } etag &&
                    EntityTagHeaderValue.TryParse(etag, out var parsedTag))
                    request.Headers.IfNoneMatch.Add(parsedTag);
                if (redirect == 0 && validators?.LastModified is { } modified)
                    request.Headers.IfModifiedSince = modified;

                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirect >= options.MaximumRedirects || response.Headers.Location is null)
                        return new(FeedFetchStatus.RedirectRejected);

                    Uri next;
                    try
                    {
                        next = FeedAddress.Validate(new Uri(current, response.Headers.Location), options.AllowHttp);
                    }
                    catch (Exception error) when (error is ArgumentException or UriFormatException)
                    {
                        return new(FeedFetchStatus.RedirectRejected);
                    }
                    if (!StringComparer.OrdinalIgnoreCase.Equals(current.IdnHost, next.IdnHost) ||
                        (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps))
                        return new(FeedFetchStatus.RedirectRejected);
                    current = next;
                    continue;
                }

                var responseEtag = response.Headers.ETag?.ToString() ?? validators?.ETag;
                var responseModified = response.Content?.Headers.LastModified ?? validators?.LastModified;

                if (response.StatusCode == HttpStatusCode.NotModified)
                    return new(FeedFetchStatus.NotModified, ETag: responseEtag, LastModified: responseModified);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return new(FeedFetchStatus.RateLimited, ETag: responseEtag,
                        LastModified: responseModified, RetryAfter: GetRetryAfter(response));
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new(FeedFetchStatus.AccessDenied, ETag: responseEtag, LastModified: responseModified);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new(FeedFetchStatus.NotFound, ETag: responseEtag, LastModified: responseModified);
                if (!response.IsSuccessStatusCode)
                    return new(FeedFetchStatus.Unavailable, ETag: responseEtag, LastModified: responseModified);

                if (response.Content is null)
                    return new(FeedFetchStatus.InvalidFeed, ETag: responseEtag, LastModified: responseModified);

                byte[] body;
                try
                {
                    body = await BoundedContentReader.ReadAsync(
                        response.Content, options.MaximumResponseBytes, deadline.Token).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    return new(FeedFetchStatus.TooLarge, ETag: responseEtag, LastModified: responseModified);
                }

                try
                {
                    var document = FeedParser.Parse(body, current, options.MaximumItems);
                    return new(FeedFetchStatus.Updated, document, responseEtag, responseModified);
                }
                catch (InvalidDataException)
                {
                    return new(FeedFetchStatus.InvalidFeed, ETag: responseEtag, LastModified: responseModified);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(FeedFetchStatus.TimedOut);
        }
        catch (HttpRequestException error)
        {
            var status = (options.ProxyAddress is not null || options.Proxy is not null) && error.HttpRequestError is
                HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ProxyTunnelError
                ? FeedFetchStatus.ProxyUnavailable : FeedFetchStatus.Unavailable;
            return new(status);
        }
        catch (IOException)
        {
            return new(FeedFetchStatus.Unavailable);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
            HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static TimeSpan GetRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter?.Delta;
        if (retry is null && response.Headers.RetryAfter?.Date is { } date)
            retry = date - DateTimeOffset.UtcNow;
        return retry is { } value && value > TimeSpan.FromSeconds(30)
            ? value
            : TimeSpan.FromSeconds(30);
    }

    public void Dispose() => http.Dispose();
}
