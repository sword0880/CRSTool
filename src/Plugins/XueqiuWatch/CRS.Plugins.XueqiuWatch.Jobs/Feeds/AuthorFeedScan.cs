using CRS.Feed;

namespace CRS.Plugins.XueqiuWatch.Jobs;

/// <summary>单个作者的一次独立 Feed 抓取结果。</summary>
internal sealed record AuthorFeedScan(string Account, string Url, FeedFetchResult Result);
