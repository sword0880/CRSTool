using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Abstractions;
using CRS.Feed;

namespace CRS.Plugins.XueqiuWatch.Jobs;

public sealed partial class XueqiuBackgroundEntry
{
    private sealed partial class Session
    {
        public Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken token) =>
            ExecuteCoreAsync(jobId, null, null, token);

        public async Task<BackgroundJobResult> ExecuteViaProxyAsync(string jobId, System.Net.IWebProxy? proxy, CancellationToken token)
        {
            using var routed = new FeedClient(options: new() { Proxy = proxy, UseSystemProxy = false });
            return await ExecuteCoreAsync(jobId, routed, proxy, token);
        }

        private async Task<BackgroundJobResult> ExecuteCoreAsync(string jobId, FeedClient? routed, System.Net.IWebProxy? proxy,
            CancellationToken token)
        {
            if (jobId != WatchProtocol.LeadJobId) return new("Rejected");

            (Author Author, FeedCoverage? Coverage, DateTimeOffset? LastComplete, AuthorBackfill? Backfill)[] sources;
            string proxyAddress;
            await gate.WaitAsync(token);
            try
            {
                var current = store ?? throw new InvalidOperationException("观点追踪尚未启动。");
                var authors = current.LeadAuthors();
                if (authors.Length == 0) return BackgroundJobResult.Success("观察名单里没有启用的作者。");
                sources = authors.Select(author => (author, current.LeadCoverage(author.Account),
                    current.TimelineCompleted(author.Account), current.PendingBackfill(author.Account))).ToArray();
                proxyAddress = routed is null ? current.ReadRssProxy() : "";
            }
            finally { gate.Release(); }

            await using var api = openOriginalPages?.Invoke(proxy);
            var timeline = api is IAuthorTimelineReader reader
                ? await DiscoverTimelinesAsync(reader,
                    sources.Select(source => (source.Author, source.LastComplete, source.Backfill)).ToArray(), token)
                : null;
            var subscriptions = sources.Where(source =>
                (source.Author.FeedUrl.Length > 0 && timeline?.SkipFallback.Contains(source.Author.Account) != true) ||
                source.Author.FullTextFeedUrl.Length > 0).ToArray();
            if (timeline is null && subscriptions.Length == 0)
                return new("Rejected", "请在「观察名单」中为需要跟踪的作者配置 RSS 地址。");

            // 备用 RSS 只用于动态未读全的作者。授权全文源可独立补全已有线索。
            using var googleFeed = proxyAddress.Length == 0 ? null :
                new FeedClient(options: new() { ProxyAddress = new Uri(proxyAddress) });
            using var admission = new SemaphoreSlim(3, 3);
            var scans = await Task.WhenAll(subscriptions.Select(async subscription =>
            {
                await admission.WaitAsync(token);
                try
                {
                    var author = subscription.Author;
                    AuthorFeedScan? leads = null;
                    if (author.FeedUrl.Length > 0 && timeline?.SkipFallback.Contains(author.Account) != true)
                    {
                        var validators = new FeedValidators(subscription.Coverage?.ETag, subscription.Coverage?.LastModified);
                        var address = new Uri(author.FeedUrl);
                        var client = routed ?? (googleFeed is not null && WatchInput.IsGoogleAlertsFeed(address) ? googleFeed : feed);
                        leads = await ReadAuthorFeedAsync(client, author.Account, author.FeedUrl, validators, token);
                    }
                    // 全文 Feed 保持独立，前次看到的正文可能刚与新线索匹配，仍需核对完整条目。
                    var fullText = author.FullTextFeedUrl.Length == 0 ? null :
                        await ReadAuthorFeedAsync(routed ?? feed, author.Account, author.FullTextFeedUrl, null, token);
                    return (Leads: leads, Bodies: fullText);
                }
                finally { admission.Release(); }
            }));

            (int Scanned, int Added, int Failed) counts;
            (int Added, int Failed) bodies;
            var leadScans = scans.Select(scan => scan.Leads).OfType<AuthorFeedScan>().ToArray();
            var fullTextScans = scans.Select(scan => scan.Bodies).OfType<AuthorFeedScan>().ToArray();
            await gate.WaitAsync(token);
            try
            {
                if (store is not { } current) return new("Rejected", "观点追踪存储已关闭。");
                counts = current.SaveLeadScans(leadScans, WatchProtocol.BeijingNow, token);
                bodies = current.SaveFullTextScans(fullTextScans, token);
            }
            finally { gate.Release(); }

            // 发现结果先落库，再用同一匿名会话补全正文。
            var originals = api is null ? default : await ReadOriginalPagesAsync(api, token);
            var limited = leadScans.Concat(fullTextScans).Where(scan => scan.Result.Status == FeedFetchStatus.RateLimited)
                .Select(scan => scan.Result.RetryAfter ?? TimeSpan.FromMinutes(30)).ToArray();
            if (originals.RetryAfter is { } retryAfter) limited = [..limited, retryAfter];
            if (timeline?.RetryAfter is { } timelineRetry && originals.RetryAfter is null)
                limited = [..limited, timelineRetry];
            var proxyFailures = leadScans.Count(scan => scan.Result.Status == FeedFetchStatus.ProxyUnavailable);
            var proxyNote = proxyFailures > 0 ? $" {proxyFailures} 个来源无法经所用代理连接。" : "";
            var fullTextNote = (fullTextScans.Length == 0 ? "" : $" 全文来源补全 {bodies.Added} 条，读取失败 {bodies.Failed} 个。") +
                originals.Note;
            if (timeline is not null)
            {
                var rssNote = counts.Scanned == 0 ? "" : $" 备用 RSS 检查 {counts.Scanned} 个，新增 {counts.Added} 条，失败 {counts.Failed} 个。";
                var checkpoint = timeline.Note + rssNote + proxyNote + fullTextNote;
                await gate.WaitAsync(token);
                try
                {
                    if (store is { } current && current.PendingBackfillCount() is > 0 and var pending)
                    {
                        checkpoint += $" {pending} 位新作者还有近 30 天动态或正文待获取。";
                        var now = WatchProtocol.BeijingNow;
                        var backfillRetry = current.ApiPause(now) is { } pause ? pause.Until - now :
                            TimeSpan.FromMinutes(sources.Any(source => source.Backfill is not null &&
                                !timeline.SkipFallback.Contains(source.Author.Account)) ? 15 : 1);
                        limited = [..limited, backfillRetry];
                    }
                }
                finally { gate.Release(); }
                if (limited.Length > 0) return new("RetryDeferred", checkpoint, limited.Max());
                return timeline.SkipFallback.Count > 0 || counts.Scanned > counts.Failed
                    ? BackgroundJobResult.Success(checkpoint)
                    : new("Failed", checkpoint + " 暂无完整的作者读取结果，已保留可用线索。");
            }
            if (limited.Length > 0)
                return new("RetryDeferred", $"已保存可用来源结果；{limited.Length} 个来源被限流，当前覆盖未知。{proxyNote}{fullTextNote}", limited.Max());

            if (counts.Scanned > 0 && counts.Failed == counts.Scanned)
                return new("Failed", $"已检查 {counts.Scanned} 个作者来源，但均未能读取；历史线索已保留。{proxyNote}{fullTextNote}");

            return BackgroundJobResult.Success(
                $"已检查 {counts.Scanned} 个作者来源，新增 {counts.Added} 条待核验线索，失败 {counts.Failed} 个。RSS 返回范围不代表作者全部发言覆盖。{proxyNote}{fullTextNote}");
        }

        private static async Task<AuthorFeedScan> ReadAuthorFeedAsync(FeedClient client, string account, string url,
            FeedValidators? validators, CancellationToken token)
        {
            try { return new(account, url, await client.FetchAsync(new Uri(url), validators, token)); }
            catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or FormatException or InvalidOperationException)
            {
                return new(account, url, new(FeedFetchStatus.Unavailable));
            }
        }
    }
}
