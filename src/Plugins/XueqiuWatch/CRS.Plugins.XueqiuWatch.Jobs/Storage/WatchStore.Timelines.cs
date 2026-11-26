using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed record TimelineScan(string State, DateTimeOffset Started, TimelinePost[] Posts, int Pages,
    TimeSpan? RetryAfter = null, int? HttpStatus = null, AuthorBackfill? Backfill = null);

internal sealed partial class WatchStore
{
    private void InitializeTimelines()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            ALTER TABLE post_leads ADD COLUMN timeline_published INTEGER;
            CREATE TABLE author_timeline(account TEXT PRIMARY KEY REFERENCES authors(account), state TEXT NOT NULL,
                observed INTEGER NOT NULL, matches INTEGER NOT NULL, pages INTEGER NOT NULL,
                completed INTEGER, retry INTEGER, http_status INTEGER);
            PRAGMA user_version=14;
            """, transaction);
        transaction.Commit();
    }

    internal DateTimeOffset? TimelineCompleted(string account) =>
        Scalar("SELECT completed FROM author_timeline WHERE account=$account", null, ("$account", account)) is long value
            ? BeijingTime(value) : null;

    internal int SaveTimeline(string account, TimelineScan scan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!LeadAuthors().Any(author => author.Account == account)) return 0;
        var observed = WatchProtocol.BeijingNow;
        using var transaction = db.BeginTransaction();
        var added = 0;
        foreach (var post in scan.Posts)
        {
            token.ThrowIfCancellationRequested();
            var url = WatchInput.SourceLink($"https://xueqiu.com/{account}/{post.Id}");
            if (SaveLead(new(account, url, observed, post.Title, post.Summary, post.Content, post.Published), transaction))
                added++;
        }
        var delay = scan.RetryAfter ?? (scan.HttpStatus == 567 ? TimeSpan.FromHours(6) : (TimeSpan?)null);
        if (scan.Backfill is { } backfill) SaveBackfill(account, backfill, transaction);
        Execute("""
            INSERT INTO author_timeline(account,state,observed,matches,pages,completed,retry,http_status)
            VALUES($account,$state,$observed,$matches,$pages,$completed,$retry,$http)
            ON CONFLICT(account) DO UPDATE SET state=excluded.state,observed=excluded.observed,
                matches=excluded.matches,pages=excluded.pages,completed=COALESCE(excluded.completed,author_timeline.completed),
                retry=excluded.retry,http_status=excluded.http_status
            """, transaction, ("$account", account), ("$state", scan.State), ("$observed", observed.ToUnixTimeSeconds()),
            ("$matches", scan.Posts.Length), ("$pages", scan.Pages),
            ("$completed", scan.State == "Read" ? (object)scan.Started.ToUnixTimeSeconds() : DBNull.Value),
            ("$retry", delay is { } wait ? (object)observed.Add(wait).ToUnixTimeSeconds() : DBNull.Value),
            ("$http", (object?)scan.HttpStatus ?? DBNull.Value));
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return added;
    }

    private Dictionary<string, TimelineCoverage> ReadTimelineCoverage()
    {
        var result = new Dictionary<string, TimelineCoverage>(StringComparer.Ordinal);
        using var command = Command("SELECT account,state,observed,matches,pages FROM author_timeline", null);
        using var rows = command.ExecuteReader();
        while (rows.Read())
            result.Add(rows.GetString(0), new(rows.GetString(1), BeijingTime(rows.GetInt64(2)), rows.GetInt32(3), rows.GetInt32(4)));
        return result;
    }
}
