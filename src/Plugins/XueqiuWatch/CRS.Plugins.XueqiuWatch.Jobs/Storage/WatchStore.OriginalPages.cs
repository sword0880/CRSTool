using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed record OriginalPageLead(string Account, string Url, long FirstSeen, long LastChecked);
internal sealed record OriginalPageRead(string State, string? Text = null, DateTimeOffset? Published = null,
    TimeSpan? RetryAfter = null);

internal sealed partial class WatchStore
{
    private void InitializeOriginalPages()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE original_page_checks(account TEXT NOT NULL, url TEXT NOT NULL, state TEXT NOT NULL,
                checked INTEGER NOT NULL, retry INTEGER NOT NULL, PRIMARY KEY(account,url),
                FOREIGN KEY(account,url) REFERENCES post_leads(account,url) ON DELETE CASCADE);
            PRAGMA user_version=12;
            """, transaction);
        transaction.Commit();
    }

    internal OriginalPageLead[] OriginalPageQueue(DateTimeOffset now, int limit)
    {
        var posts = new List<OriginalPageLead>();
        foreach (var author in LeadAuthors().Where(UsesGoogleAlerts))
        {
            using var command = Command("""
                SELECT p.url,p.first_seen,COALESCE(s.checked,0) FROM post_leads p
                LEFT JOIN original_page_checks s ON s.account=p.account AND s.url=p.url
                WHERE p.account=$account AND COALESCE(s.retry,0)<=$now
                AND NOT EXISTS(SELECT 1 FROM post_bodies b WHERE b.account=p.account AND b.url=p.url)
                ORDER BY COALESCE(s.checked,0),p.first_seen DESC,p.url LIMIT $limit
                """, null, ("$account", author.Account), ("$now", now.ToUnixTimeSeconds()), ("$limit", limit));
            using var rows = command.ExecuteReader();
            while (rows.Read()) posts.Add(new(author.Account, rows.GetString(0), rows.GetInt64(1), rows.GetInt64(2)));
        }
        return posts.OrderBy(post => post.LastChecked).ThenByDescending(post => post.FirstSeen)
            .ThenBy(post => post.Url, StringComparer.Ordinal).Take(limit).ToArray();
    }

    internal bool SaveOriginalPage(OriginalPageLead post, OriginalPageRead result, DateTimeOffset now, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!LeadAuthors().Any(author => author.Account == post.Account && UsesGoogleAlerts(author))) return false;
        using var transaction = db.BeginTransaction();
        var state = result.State;
        var saved = state == "FullText" && result.Text is { } text &&
            result.Published is { } published && published.Year >= 2005 && published <= now.AddMinutes(5) &&
            TrySaveAutomaticBody(post.Account, post.Url, published, text, PostBodySource.OriginalPage, transaction);
        if (!saved && state == "FullText") state = "BodyUnavailable";
        var delay = result.RetryAfter ?? TimeSpan.FromHours(state is "BodyUnavailable" or "AccessRestricted" ? 24 : 2);
        Execute("""
            INSERT INTO original_page_checks(account,url,state,checked,retry) VALUES($a,$u,$s,$c,$retry)
            ON CONFLICT(account,url) DO UPDATE SET state=excluded.state,checked=excluded.checked,retry=excluded.retry
            """, transaction, ("$a", post.Account), ("$u", post.Url), ("$s", state), ("$c", now.ToUnixTimeSeconds()),
            ("$retry", now.Add(delay).ToUnixTimeSeconds()));
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return saved;
    }

    private static bool UsesGoogleAlerts(Author author) =>
        Uri.TryCreate(author.FeedUrl, UriKind.Absolute, out var feed) && WatchInput.IsGoogleAlertsFeed(feed);
}
