using System.Security.Cryptography;
using CRS.Feed;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    private void InitializeFullTextFeeds()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            ALTER TABLE post_bodies ADD COLUMN origin INTEGER NOT NULL DEFAULT 0;
            CREATE TABLE full_text_coverage(account TEXT PRIMARY KEY REFERENCES authors(account),
                state TEXT NOT NULL, added INTEGER NOT NULL);
            PRAGMA user_version=10;
            """, transaction);
        transaction.Commit();
    }

    internal (int Added, int Failed) SaveFullTextScans(IEnumerable<AuthorFeedScan> scans, CancellationToken token)
    {
        var active = LeadAuthors().ToDictionary(author => author.Account, StringComparer.Ordinal);
        using var transaction = db.BeginTransaction();
        var added = 0;
        var failed = 0;
        foreach (var scan in scans)
        {
            token.ThrowIfCancellationRequested();
            if (!active.TryGetValue(scan.Account, out var author) || author.FeedUrl.Length == 0 ||
                author.FullTextFeedUrl != scan.Url)
                continue;

            var state = CoverageState(scan.Result.Status);
            var count = 0;
            if (scan.Result.Status == FeedFetchStatus.Updated && scan.Result.Feed is { } feed)
            {
                foreach (var item in feed.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (TrySaveFullText(item, scan.Account, transaction)) count++;
                }
            }
            else if (state != "NotModified") failed++;
            Execute("""
                INSERT INTO full_text_coverage(account,state,added) VALUES($account,$state,$added)
                ON CONFLICT(account) DO UPDATE SET state=excluded.state,added=excluded.added
                """, transaction, ("$account", scan.Account), ("$state", state), ("$added", count));
            added += count;
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return (added, failed);
    }

    private bool TrySaveFullText(FeedItem item, string account, SqliteTransaction transaction)
    {
        if (item.Url is null || string.IsNullOrWhiteSpace(item.PlainTextContent) ||
            item.PublishedAt is not { } published || published.Year < 2005 ||
            published > WatchProtocol.BeijingNow.AddMinutes(5))
            return false;
        string url;
        try
        {
            url = WatchInput.AlertSourceLink(item.Url.AbsoluteUri, account);
        }
        catch (ArgumentException) { return false; }

        return TrySaveAutomaticBody(account, url, published, item.PlainTextContent, PostBodySource.AuthorizedFeed, transaction);
    }

    private bool TrySaveAutomaticBody(string account, string url, DateTimeOffset published, string body,
        PostBodySource source, SqliteTransaction transaction)
    {
        byte[] bytes;
        try { bytes = PostBody.Encode(body); }
        catch (ArgumentException) { return false; }
        // 网页与供稿共用入库规则，只为已发现且尚无任何正文版本的原帖写入。
        if (Scalar("""
            SELECT 1 FROM post_leads p WHERE p.account=$account AND p.url=$url
            AND NOT EXISTS(SELECT 1 FROM post_bodies b WHERE b.account=p.account AND b.url=p.url)
            """, transaction, ("$account", account), ("$url", url)) is null)
            return false;
        WritePostBody(new(account, url), 1, published.ToUnixTimeSeconds(), Convert.ToHexString(SHA256.HashData(bytes)),
            bytes, source, transaction);
        return true;
    }

    private Dictionary<string, (string State, int Added)> ReadFullTextCoverage()
    {
        using var command = Command("SELECT account,state,added FROM full_text_coverage", null);
        using var rows = command.ExecuteReader();
        var results = new Dictionary<string, (string State, int Added)>(StringComparer.Ordinal);
        while (rows.Read()) results.Add(rows.GetString(0), (rows.GetString(1), rows.GetInt32(2)));
        return results;
    }
}
