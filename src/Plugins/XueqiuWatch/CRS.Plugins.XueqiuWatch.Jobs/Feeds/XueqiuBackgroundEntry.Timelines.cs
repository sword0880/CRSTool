using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Jobs;

public sealed partial class XueqiuBackgroundEntry
{
    private sealed partial class Session
    {
        private sealed record TimelineRun(HashSet<string> SkipFallback, TimeSpan? RetryAfter, string Note);

        private async Task<TimelineRun> DiscoverTimelinesAsync(IAuthorTimelineReader reader,
            (Author Author, DateTimeOffset? LastComplete, AuthorBackfill? Backfill)[] authors, CancellationToken token)
        {
            var skipFallback = new HashSet<string>(StringComparer.Ordinal);
            var attempted = 0;
            var added = 0;
            var completed = 0;
            var partiallyRead = 0;
            var consecutiveFailures = 0;
            TimeSpan? retry = null;
            var pause = "";
            var sentRequest = false;
            var remainingBackfillPages = 24;
            foreach (var (author, lastComplete, backfill) in authors.OrderByDescending(author => author.Backfill is not null))
            {
                if (backfill is not null && remainingBackfillPages == 0)
                {
                    skipFallback.Add(author.Account);
                    continue;
                }
                await gate.WaitAsync(token);
                try
                {
                    var now = WatchProtocol.BeijingNow;
                    if (store?.ApiPause(now) is { } blocked)
                    {
                        if (blocked.Status == 429) retry = blocked.Until - now;
                        pause = $" 雪球 API（HTTP {blocked.Status}）暂停至 {blocked.Until:yyyy-MM-dd HH:mm}。";
                        break;
                    }
                }
                finally { gate.Release(); }

                var scan = backfill is null
                    ? await ScanAuthorAsync(author.Account, lastComplete, ReadPageAsync, token)
                    : await ReadBackfillAsync(author.Account, backfill, Math.Min(8, remainingBackfillPages), ReadPageAsync, token);
                if (backfill is not null) remainingBackfillPages -= Math.Max(scan.Pages, 1);
                attempted++;
                if (scan.State is "Read" or "Backfilling") skipFallback.Add(author.Account);
                if (scan.State == "Read") completed++;
                if (scan.State != "Read" && scan.Pages > 0) partiallyRead++;
                await gate.WaitAsync(token);
                try { added += store?.SaveTimeline(author.Account, scan, token) ?? 0; }
                finally { gate.Release(); }

                if (scan.HttpStatus is 429 or 567)
                {
                    retry = scan.RetryAfter;
                    pause = $" 雪球 API（HTTP {scan.HttpStatus}）暂停，剩余作者转查备用 RSS。";
                    break;
                }
                consecutiveFailures = scan is { State: "Unavailable", Pages: 0 } ? consecutiveFailures + 1 : 0;
                if (consecutiveFailures >= 3)
                {
                    pause = " 雪球 API 连续读取失败，剩余作者转查备用 RSS。";
                    break;
                }
            }
            return new(skipFallback, retry,
                $"雪球 API 检查 {attempted}/{authors.Length} 位作者，完成 {completed} 位，部分读取 {partiallyRead} 位，新增 {added} 条。{pause}");

            async Task<TimelinePage> ReadPageAsync(string account, int page, CancellationToken cancellation)
            {
                if (sentRequest) await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
                sentRequest = true;
                return await reader.ReadTimelineAsync(account, page, cancellation);
            }
        }

        private static async Task<TimelineScan> ReadBackfillAsync(string account, AuthorBackfill current, int batchSize,
            Func<string, int, CancellationToken, Task<TimelinePage>> read, CancellationToken token)
        {
            var end = current.WindowEnd ?? WatchProtocol.BeijingNow;
            var cutoff = end.AddDays(-WatchProtocol.NewAuthorHistoryDays);
            var saved = current with { WindowEnd = end };
            var joined = current.Page == 0;
            var first = Math.Max(1, current.Page - 1);
            var posts = new Dictionary<string, TimelinePost>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pages = 0;
            for (var page = first; page < first + batchSize; page++)
            {
                var result = await read(account, page, token);
                if (result.State != "Read")
                    return Result(result.State, result.RetryAfter, result.HttpStatus);

                pages++;
                var hasNewId = false;
                foreach (var post in result.Posts)
                {
                    hasNewId |= seen.Add(post.Id);
                    if (post.Published >= cutoff && post.Published <= end) posts.TryAdd(post.Id, post);
                }

                var terminal = result.LastPage is > 0 && page >= result.LastPage;
                var empty = result.Returned == 0;
                if (result.Posts.Length != result.Returned || (empty && !terminal && result.LastPage is > 0))
                    return Result("Partial");
                var pastCutoff = page > 1 && !empty && result.Posts.All(post => post.Published < cutoff);
                joined |= result.Posts.Any(post => current.Anchors.Contains(post.Id, StringComparer.Ordinal));
                if (!joined)
                {
                    if (terminal || empty || pastCutoff) break;
                    continue;
                }

                if (!empty && !hasNewId && !pastCutoff) return Result("Partial");
                if (terminal || empty || pastCutoff)
                {
                    saved = saved with { Discovered = true };
                    return Result("Read");
                }

                // 续页先重读锚点附近。页码因新帖、删帖移动时，必须找到重叠的已存帖子才能继续推进。
                var anchors = result.Posts.TakeLast(3).Select(post => post.Id).Distinct(StringComparer.Ordinal).ToArray();
                if (page >= saved.Page)
                    saved = saved with { Page = page, Anchors = anchors };
            }
            if (joined) return Result("Backfilling");
            saved = saved with { Page = 0, Anchors = [] };
            return Result("Partial");

            TimelineScan Result(string state, TimeSpan? retry = null, int? status = null) =>
                new(state, end, posts.Values.ToArray(), pages, retry, status, saved);
        }

        private static async Task<TimelineScan> ScanAuthorAsync(string account, DateTimeOffset? lastComplete,
            Func<string, int, CancellationToken, Task<TimelinePage>> read, CancellationToken token)
        {
            var started = WatchProtocol.BeijingNow;
            var cutoff = lastComplete?.AddHours(-12);
            var limit = cutoff is null ? 2 : 8;
            var found = new Dictionary<string, TimelinePost>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pages = 0;
            var invalidEntries = false;
            for (var page = 1; page <= limit; page++)
            {
                var result = await read(account, page, token);
                if (result.State != "Read")
                    return new(result.State, started, found.Values.ToArray(), pages, result.RetryAfter, result.HttpStatus);

                pages++;
                var hasNewId = false;
                foreach (var post in result.Posts)
                {
                    hasNewId |= seen.Add(post.Id);
                    if (cutoff is null || post.Published >= cutoff) found.TryAdd(post.Id, post);
                }
                invalidEntries |= result.Posts.Length != result.Returned;
                var terminal = result.LastPage is > 0 && page >= result.LastPage;
                if (result.Returned == 0)
                    return Finish((result.LastPage is null or 0 || terminal) && !invalidEntries);
                if (!hasNewId && !terminal) return Finish(false);
                // 短页后仍可能有帖子。整页都早于回查边界才结束，避免置顶旧帖造成提前停止。
                if (terminal || (cutoff is { } boundary && result.Posts.All(post => post.Published < boundary)))
                    return Finish(!invalidEntries);
            }
            return Finish(cutoff is null && !invalidEntries);

            TimelineScan Finish(bool complete) => new(complete ? "Read" : "Partial", started, found.Values.ToArray(), pages);
        }
    }
}
