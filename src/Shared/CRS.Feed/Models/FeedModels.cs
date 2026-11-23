namespace CRS.Feed;

/// <summary>Feed 请求的结果状态。</summary>
public enum FeedFetchStatus
{
    Updated, NotModified, RateLimited, AccessDenied, NotFound,
    RedirectRejected, TooLarge, InvalidFeed, TimedOut, Unavailable
}

/// <summary>用于条件请求的缓存验证器。</summary>
public sealed record FeedValidators(string? ETag = null, DateTimeOffset? LastModified = null);

/// <summary>标准化后的 Feed 文档。</summary>
public sealed record FeedDocument(
    string? Title, string? Description, Uri? HomePageUrl, Uri FeedUrl, FeedItem[] Items);

/// <summary>标准化后的单篇文章。正文可能包含不可信 HTML，展示前必须由调用方清理。</summary>
public sealed record FeedItem(
    string Id, string Title, Uri? Url, string? Summary, string? Content, string? Author,
    DateTimeOffset? PublishedAt, DateTimeOffset? UpdatedAt, string[] Categories);

/// <summary>单次 Feed 抓取结果，不暴露原始异常或响应正文。</summary>
public sealed record FeedFetchResult(
    FeedFetchStatus Status, FeedDocument? Feed = null, string? ETag = null,
    DateTimeOffset? LastModified = null, TimeSpan? RetryAfter = null);

/// <summary>RSS Engine 的资源与网络限制。</summary>
public sealed record FeedClientOptions
{
    public int MaximumResponseBytes { get; init; } = 512 * 1024;
    public int MaximumItems { get; init; } = 200;
    public int MaximumRedirects { get; init; } = 3;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(12);
    public bool AllowHttp { get; init; }
    public string UserAgent { get; init; } = "CRSTool/1.0";

    internal void Validate()
    {
        if (MaximumResponseBytes is < 1024 or > 5 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        if (MaximumItems is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(MaximumItems));
        if (MaximumRedirects is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(MaximumRedirects));
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (string.IsNullOrWhiteSpace(UserAgent) || UserAgent.Length > 120)
            throw new ArgumentException("Feed User-Agent 配置无效。", nameof(UserAgent));
    }
}
