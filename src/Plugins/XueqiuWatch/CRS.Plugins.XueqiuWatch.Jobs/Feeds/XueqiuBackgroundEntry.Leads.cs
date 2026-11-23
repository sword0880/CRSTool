using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Abstractions;
using CRS.Feed;

namespace CRS.Plugins.XueqiuWatch.Jobs;

public sealed partial class XueqiuBackgroundEntry
{
    private sealed partial class Session
    {
        public async Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken token)
        {
            if (jobId != WatchProtocol.LeadJobId) return new("Rejected");

            (Author Author, FeedCoverage? Coverage)[] subscriptions;
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
            }
            finally { gate.Release(); }

            // 每位作者独立抓取，单个来源的异常不会阻断其他作者；共享客户端并限制并发数。
            using var admission = new SemaphoreSlim(3, 3);
            var scans = await Task.WhenAll(subscriptions.Select(async subscription =>
            {
                await admission.WaitAsync(token);
                try
                {
                    var validators = new FeedValidators(subscription.Coverage?.ETag, subscription.Coverage?.LastModified);
                    FeedFetchResult result;
                    try
                    {
                        result = await feed.FetchAsync(new Uri(subscription.Author.FeedUrl), validators, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or UriFormatException or InvalidOperationException)
                    {
                        // 单个来源出现网络、地址或解析异常时记录为不可用，继续处理其他作者。
                        result = new FeedFetchResult(FeedFetchStatus.Unavailable);
                    }
                    return new AuthorFeedScan(subscription.Author.Account, subscription.Author.FeedUrl, result);
                }
                finally { admission.Release(); }
            }));

            (int Scanned, int Added, int Failed) counts;
            await gate.WaitAsync(token);
            try
            {
                if (store is not { } current) return new("Rejected", "观点追踪存储已关闭。");
                counts = current.SaveLeadScans(scans, WatchProtocol.BeijingNow, token);
            }
            finally { gate.Release(); }

            var limited = scans.Where(scan => scan.Result.Status == FeedFetchStatus.RateLimited)
                .Select(scan => scan.Result.RetryAfter ?? TimeSpan.FromMinutes(30)).ToArray();
            if (limited.Length > 0)
                return new("RetryDeferred", $"已保存可用来源结果；{limited.Length} 个来源被限流，当前覆盖未知。", limited.Max());

            if (counts.Scanned > 0 && counts.Failed == counts.Scanned)
                return new("Failed", $"已检查 {counts.Scanned} 个作者来源，但均未能读取；历史线索已保留。");

            return BackgroundJobResult.Success(
                $"已检查 {counts.Scanned} 个作者来源，新增 {counts.Added} 条待核验线索，失败 {counts.Failed} 个。RSS 返回范围不代表作者全部发言覆盖。");
        }
    }
}
