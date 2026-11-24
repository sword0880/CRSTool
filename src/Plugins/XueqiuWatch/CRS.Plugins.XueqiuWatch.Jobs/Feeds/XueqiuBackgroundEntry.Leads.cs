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

            (Author Author, FeedCoverage? Coverage)[] subscriptions;
            string proxyAddress;
            await gate.WaitAsync(token);
            try
            {
                var current = store ?? throw new InvalidOperationException("观点追踪尚未启动。");
                var authors = current.LeadAuthors();
                if (authors.Length == 0) return BackgroundJobResult.Success("观察名单里没有启用的作者。");
                subscriptions = authors.Where(author => !string.IsNullOrWhiteSpace(author.FeedUrl))
                    .Select(author => (author, current.LeadCoverage(author.Account))).ToArray();
                if (subscriptions.Length == 0)
                    return new("Rejected", "请在「观察名单」中为需要跟踪的作者配置 RSS 地址。");
                proxyAddress = routed is null ? current.ReadRssProxy() : "";
            }
            finally { gate.Release(); }

            // 每位作者独立抓取，单个来源的异常不会阻断其他作者；共享客户端并限制并发数。
            using var googleFeed = proxyAddress.Length == 0 ? null :
                new FeedClient(options: new() { ProxyAddress = new Uri(proxyAddress) });
            using var admission = new SemaphoreSlim(3, 3);
            var scans = await Task.WhenAll(subscriptions.Select(async subscription =>
            {
                await admission.WaitAsync(token);
                try
                {
                    var validators = new FeedValidators(subscription.Coverage?.ETag, subscription.Coverage?.LastModified);
                    var author = subscription.Author;
                    var address = new Uri(author.FeedUrl);
                    var client = routed ?? (googleFeed is not null && WatchInput.IsGoogleAlertsFeed(address) ? googleFeed : feed);
                    var leads = await ReadAuthorFeedAsync(client, author.Account, author.FeedUrl, validators, token);
                    // 全文 Feed 保持独立，前次看到的正文可能刚与新线索匹配，仍需核对完整条目。
                    var fullText = author.FullTextFeedUrl.Length == 0 ? null :
                        await ReadAuthorFeedAsync(routed ?? feed, author.Account, author.FullTextFeedUrl, null, token);
                    return (Leads: leads, Bodies: fullText);
                }
                finally { admission.Release(); }
            }));

            (int Scanned, int Added, int Failed) counts;
            (int Added, int Failed) bodies;
            var leadScans = scans.Select(scan => scan.Leads).ToArray();
            var fullTextScans = scans.Select(scan => scan.Bodies).OfType<AuthorFeedScan>().ToArray();
            await gate.WaitAsync(token);
            try
            {
                if (store is not { } current) return new("Rejected", "观点追踪存储已关闭。");
                counts = current.SaveLeadScans(leadScans, WatchProtocol.BeijingNow, token);
                bodies = current.SaveFullTextScans(fullTextScans, token);
            }
            finally { gate.Release(); }

            // 先提交 RSS 线索和供稿正文，再尝试补齐仍缺的原帖全文。浏览器失败不撤销已保存的线索。
            var originals = openOriginalPages is null ? default : await ReadOriginalPagesAsync(proxy, token);
            var limited = leadScans.Concat(fullTextScans).Where(scan => scan.Result.Status == FeedFetchStatus.RateLimited)
                .Select(scan => scan.Result.RetryAfter ?? TimeSpan.FromMinutes(30)).ToArray();
            if (originals.RetryAfter is { } retryAfter) limited = [..limited, retryAfter];
            var proxyFailures = leadScans.Count(scan => scan.Result.Status == FeedFetchStatus.ProxyUnavailable);
            var proxyNote = proxyFailures > 0 ? $" {proxyFailures} 个来源无法经所用代理连接。" : "";
            var fullTextNote = (fullTextScans.Length == 0 ? "" : $" 全文来源补全 {bodies.Added} 条，读取失败 {bodies.Failed} 个。") +
                originals.Note;
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
