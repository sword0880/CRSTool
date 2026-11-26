using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Common.Net;
using CRS.Common.Text;
using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal interface IOriginalPageReader : IAsyncDisposable
{
    Task<OriginalPageRead> ReadAsync(OriginalPageLead post, CancellationToken token);
}

internal interface IAuthorTimelineReader
{
    Task<TimelinePage> ReadTimelineAsync(string account, int page, CancellationToken token);
}

internal sealed record TimelinePost(string Id, string Title, string Summary, string Content, DateTimeOffset Published);
internal sealed record TimelinePage(string State, TimelinePost[] Posts, int Returned = 0, int? LastPage = null,
    TimeSpan? RetryAfter = null, int? HttpStatus = null);

/// <summary>在同一任务中复用匿名会话，读取雪球公开动态和帖子详情。</summary>
internal sealed class XueqiuPostApi : IOriginalPageReader, IAuthorTimelineReader
{
    private static readonly Uri Home = new("https://xueqiu.com/");
    private static readonly Uri SessionAddress = new("https://www.xueqiu.com/hq");
    private static readonly Uri DetailAddress = new("https://api.xueqiu.com/statuses/show.json");
    private static readonly Uri TimelineAddress = new("https://api.xueqiu.com/v4/statuses/user_timeline.json");
    private static readonly Regex MoreText = new(
        @"(?:展开全文|阅读全文|查看全文)(?:[ \t]*[>＞]?(?:\n|$))",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly long EarliestPostTime = new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient http;
    private bool hasSession;

    internal XueqiuPostApi(HttpMessageHandler transport)
    {
        http = new(transport, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        http.DefaultRequestHeaders.Referrer = Home;
    }

    internal static XueqiuPostApi Open(IWebProxy? route) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseProxy = route is not null,
        Proxy = route
    });

    public Task<OriginalPageRead> ReadAsync(OriginalPageLead post, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string url;
        try
        {
            url = WatchInput.SourceLink(post.Url);
            if (!url.StartsWith($"https://xueqiu.com/{WatchInput.AccountId(post.Account)}/", StringComparison.Ordinal))
                return Task.FromResult(new OriginalPageRead("BodyUnavailable"));
        }
        catch (ArgumentException) { return Task.FromResult(new OriginalPageRead("BodyUnavailable")); }
        var id = url[(url.LastIndexOf('/') + 1)..];
        var address = new UriBuilder(DetailAddress) { Query = $"id={id}" }.Uri;
        return RequestAsync(address, json => ReadDetail(json, post.Account, id), failure => failure, token);
    }

    public Task<TimelinePage> ReadTimelineAsync(string account, int page, CancellationToken token)
    {
        var id = WatchInput.AccountId(account);
        var address = new UriBuilder(TimelineAddress) { Query = $"user_id={id}&count=20&page={page}" }.Uri;
        return RequestAsync(address, json => ReadTimeline(json, id), failure =>
            new TimelinePage(failure.State == "BodyUnavailable" ? "Unavailable" : failure.State, [],
                RetryAfter: failure.RetryAfter, HttpStatus: failure.HttpStatus), token);
    }

    private async Task<T> RequestAsync<T>(Uri address, Func<JsonElement, T> read,
        Func<OriginalPageRead, T> fail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (!hasSession)
                {
                    // 只接受雪球下发的临时匿名 Cookie，会话与详情请求使用同一条任务线路。
                    using var session = await GetAsync(SessionAddress, "text/html", deadline.Token);
                    if (ResponseState(session) is { } failure) return fail(failure);
                    hasSession = true;
                }

                using var response = await GetAsync(address, "application/json", deadline.Token);
                var result = ResponseState(response);
                if (result is null)
                {
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                        return fail(new("AccessRestricted"));
                    if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                        mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) != true)
                        return fail(new("Unavailable"));
                    var data = await BoundedContentReader.ReadAsync(response.Content, MaximumResponseBytes, deadline.Token);
                    using var document = JsonDocument.Parse(data);
                    var json = document.RootElement;
                    if (json.ValueKind != JsonValueKind.Object) return fail(new("Unavailable"));
                    result = BusinessError(json);
                    if (result is null) return read(json);
                }
                if (result is not { HttpStatus: 400 or 401 } && result.State != "SessionExpired") return fail(result);
                hasSession = false;
                if (attempt != 0)
                    return fail(result.State == "SessionExpired" ? new("AccessRestricted") : result);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return fail(new("Unavailable")); }
        catch (InvalidDataException) { return fail(new("BodyUnavailable")); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        { return fail(new("Unavailable")); }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri address, string accept, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.ParseAdd(accept);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static OriginalPageRead? ResponseState(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status == 429)
        {
            var delay = HttpRetryAfter.GetDelay(response.Headers.RetryAfter, DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(30);
            return new("RateLimited", RetryAfter: TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 60, 86400)),
                HttpStatus: status);
        }
        if (status is >= 300 and < 400 or 401 or 403 or 418 or 432 or 446 or 451 or 567)
            return new("AccessRestricted", HttpStatus: status);
        if (status is 404 or 410) return new("BodyUnavailable", HttpStatus: status);
        return status == 200 ? null : new("Unavailable", HttpStatus: status);
    }

    private static OriginalPageRead? BusinessError(JsonElement status)
    {
        if (status.TryGetProperty("error_code", out var error) && error.ValueKind != JsonValueKind.Null &&
            Scalar(error) is not ("0" or ""))
            return Scalar(error) switch
            {
                "10020" => new("SessionExpired"),
                "10022" => new("AccessRestricted"),
                _ => new("Unavailable")
            };
        return null;
    }

    private static OriginalPageRead ReadDetail(JsonElement status, string author, string id)
    {
        if (Scalar(Property(status, "id")) != id || !IsAuthor(status, author) ||
            PublishedTime(status, WatchProtocol.BeijingNow) is not { } published)
            return new("BodyUnavailable");
        // 只读取该帖的正文，避免把转发原帖写成当前作者发言。
        var body = HtmlText.ToPlainText(StringProperty(status, "text"), preserveLineBreaks: true);
        var truncated = Property(status, "truncated");
        if (body.Length == 0 || Encoding.UTF8.GetByteCount(body) > PostBody.MaximumBytes ||
            truncated.ValueKind == JsonValueKind.True || Scalar(truncated) is "1" or "true" ||
            MoreText.IsMatch(body) || (body.Length <= 200 && !string.IsNullOrWhiteSpace(StringProperty(status, "title"))))
            return new("BodyUnavailable", Published: published);
        return new("FullText", body, published);
    }

    private static TimelinePage ReadTimeline(JsonElement json, string author)
    {
        var statuses = Property(json, "statuses");
        if (statuses.ValueKind != JsonValueKind.Array || statuses.GetArrayLength() > 100)
            return new("Unavailable", []);
        var now = WatchProtocol.BeijingNow;
        var posts = new List<TimelinePost>();
        foreach (var status in statuses.EnumerateArray())
        {
            var id = Scalar(Property(status, "id"));
            if (id is not { Length: >= 1 and <= 20 } || !id.All(char.IsAsciiDigit) || id.All(c => c == '0') ||
                !IsAuthor(status, author) || PublishedTime(status, now) is not { } published)
                continue;
            posts.Add(new(id, StringProperty(status, "title") ?? "", StringProperty(status, "description") ?? "",
                StringProperty(status, "text") ?? "", published));
        }
        var lastPage = int.TryParse(Scalar(Property(json, "maxPage")), NumberStyles.None,
            CultureInfo.InvariantCulture, out var maxPage) ? maxPage : (int?)null;
        return new("Read", posts.ToArray(), statuses.GetArrayLength(), lastPage);
    }

    private static bool IsAuthor(JsonElement status, string author)
    {
        var direct = Property(status, "user_id");
        var nested = Property(Property(status, "user"), "id");
        return (!Missing(direct) || !Missing(nested)) &&
            (Missing(direct) || Scalar(direct) == author) && (Missing(nested) || Scalar(nested) == author);
    }

    private static DateTimeOffset? PublishedTime(JsonElement status, DateTimeOffset now) =>
        long.TryParse(Scalar(Property(status, "created_at")), NumberStyles.None, CultureInfo.InvariantCulture, out var stamp) &&
        stamp >= EarliestPostTime && stamp <= now.AddMinutes(5).ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(stamp) : null;

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static string? StringProperty(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;

    private static bool Missing(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    public ValueTask DisposeAsync()
    {
        http.Dispose();
        return ValueTask.CompletedTask;
    }
}
