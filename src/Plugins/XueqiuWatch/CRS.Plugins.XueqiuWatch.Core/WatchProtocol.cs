using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CRS.Plugins.XueqiuWatch.Core;

public enum Stance { Pending, Bullish, Bearish, Neutral }
public enum Attribution { Author, Mention, Repost }

public sealed record Author(string Account, string Name, string Note = "", bool Active = true,
    bool Archived = false, long Revision = 0, string FeedUrl = "", GoogleAlertState AlertState = GoogleAlertState.None,
    string FullTextFeedUrl = "", DateTimeOffset? GoogleAlertVerifiedAt = null)
{
    public bool UsesGoogleAlertFeed => Uri.TryCreate(FeedUrl, UriKind.Absolute, out var feed) && WatchInput.IsGoogleAlertsFeed(feed);
}

public sealed record Security(string Market, string Code, string Name, string Industry)
{
    public string Key => $"{Market}:{Code}";
}

public sealed record Opinion(Guid Id, string Account, Security? Security, DateTimeOffset PublishedAt,
    Stance Stance, Attribution Attribution, string Content, string Evidence, string Url,
    string Question = "", string Finding = "", long Revision = 0, DateTimeOffset RecordedAt = default,
    long PostBodyRevision = 0);

public sealed record ReviewFilter(int Days = 30, string? Account = null, string? Market = null,
    string? Industry = null, string? Search = null, Security? Security = null, string Mode = "latest", int Offset = 0);

public sealed record StockSummary(Security Security, int Bullish, int Bearish, int Discussions,
    int Pending, DateTimeOffset LatestAt);
public sealed record WatchOverview(StockSummary[] Stocks, string[] Industries, int StockCount,
    int Unmatched, DateTimeOffset? Earliest, DateTimeOffset? Latest, DateTimeOffset? LastRecorded);
public sealed record OpinionDetail(Opinion[] Rows, int Total);
public sealed record WatchRequest(string Operation, ReviewFilter? Filter = null, Author? Author = null,
    Opinion? Opinion = null, Guid ChangeId = default, GoogleAlertUpdate? Alert = null, string? RssProxy = null,
    PostBodyRequest? Body = null, string? ConnectedGoogleAccount = null, string? ExpectedGoogleAccount = null);

public sealed record WatchReply(WatchOverview? Overview = null, OpinionDetail? Detail = null,
    Author[]? Authors = null, Security[]? Securities = null, string? Error = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LeadPage? Leads { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleAlertSubscription? Alert { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RssProxy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PostBodyInfo? Body { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? BodyBytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectedGoogleAccount { get; init; }
}

public sealed record PostLead(string Account, string Url, DateTimeOffset FirstSeen, string Title = "",
    string Summary = "", string Content = "", DateTimeOffset? PublishedAt = null, string? FeedAuthor = null,
    string FeedItemId = "", long PostBodyRevision = 0, PostBodySource BodySource = PostBodySource.Manual)
{
    public string? OriginalPageState { get; init; }
}
public sealed record FeedCoverage(string Account, string State, DateTimeOffset? CheckedAt, int? Matches,
    string? ETag = null, DateTimeOffset? LastModified = null, string? FullTextState = null,
    int? FullTextCount = null);
public sealed record LeadPage(bool Configured, Author[] Authors, FeedCoverage[] Coverage, PostLead[] Rows, int Total,
    int ConfiguredFeeds = 0, int UnconfiguredFeeds = 0);

public static class WatchProtocol
{
    public const string PluginId = "crs.xueqiu-watch";
    public const string LeadJobId = "discover-post-leads";
    public const int LeadsPerPage = 25;
    public const int MaximumPayload = 4096;
    public const int StocksPerPage = 40;
    public const int OpinionsPerPage = 6;
    public const int MaximumAuthors = 54;

    public static readonly string[] Markets = ["沪市", "深市", "北交所", "港股", "美股"];
    public static DateTimeOffset BeijingNow => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IgnoreReadOnlyProperties = true
    };

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new InvalidDataException("观点追踪数据为空。");

    public static IReadOnlyList<Author> InitialAuthors { get; } = Array.AsReadOnly<Author>(
    [
        new("1556808774", "宁静的冬日M"), new("8790885129", "超级鹿鼎公"),
        new("9650668145", "管我财"), new("2681290304", "学知利行"),
        new("1760673340", "HIS1963"), new("6451611049", "润哥"),
        new("3045778209", "大黄蚬子hgx"), new("8632046745", "达人知命", "雪球昵称为達人知命"),
        new("8799248060", "股市小民a")
    ]);
}
