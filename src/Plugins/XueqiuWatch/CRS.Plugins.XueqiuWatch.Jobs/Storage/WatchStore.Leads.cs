using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Feed;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    private void InitializeLeads()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE lead_feed(id INTEGER PRIMARY KEY CHECK(id=1), url TEXT NOT NULL);
            CREATE TABLE lead_coverage(account TEXT PRIMARY KEY REFERENCES authors(account),
                state TEXT NOT NULL, observed INTEGER NOT NULL, matches INTEGER NOT NULL);
            CREATE TABLE post_leads(account TEXT NOT NULL REFERENCES authors(account), url TEXT NOT NULL,
                first_seen INTEGER NOT NULL, PRIMARY KEY(account,url));
            CREATE INDEX post_leads_recent ON post_leads(first_seen DESC,account,url);
            PRAGMA user_version=2;
            """, transaction);
        transaction.Commit();
    }

    private void InitializeLeadScope()
    {
        using var transaction = db.BeginTransaction();
        Execute("ALTER TABLE lead_feed ADD COLUMN scope TEXT; PRAGMA user_version=3;", transaction);
        transaction.Commit();
    }

    private void InitializeAuthorFeeds()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            ALTER TABLE lead_coverage ADD COLUMN etag TEXT;
            ALTER TABLE lead_coverage ADD COLUMN last_modified TEXT;
            DROP TABLE IF EXISTS lead_feed;
            UPDATE lead_coverage
            SET state='NeverChecked', observed=0, matches=-1, etag=NULL, last_modified=NULL;
            PRAGMA user_version=4;
            """, transaction);
        transaction.Commit();
    }

    private void InitializeLeadArticleMetadata()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            ALTER TABLE post_leads ADD COLUMN title TEXT NOT NULL DEFAULT '';
            ALTER TABLE post_leads ADD COLUMN summary TEXT NOT NULL DEFAULT '';
            ALTER TABLE post_leads ADD COLUMN content TEXT NOT NULL DEFAULT '';
            ALTER TABLE post_leads ADD COLUMN published INTEGER;
            ALTER TABLE post_leads ADD COLUMN feed_author TEXT;
            ALTER TABLE post_leads ADD COLUMN feed_item_id TEXT NOT NULL DEFAULT '';
            PRAGMA user_version=5;
            """, transaction);
        transaction.Commit();
    }

    private void InitializeTrackedAuthors()
    {
        using var transaction = db.BeginTransaction();
        // 补齐新增的固定观察作者；已存在的用户配置和暂停状态不覆盖。
        var newAuthor = WatchProtocol.InitialAuthors.Single(author => author.Account == "8799248060");
        if (Scalar("SELECT body FROM authors WHERE account=$account", transaction,
            ("$account", newAuthor.Account)) is null)
            Execute("INSERT INTO authors(account,body) VALUES($account,$body)", transaction,
                ("$account", newAuthor.Account),
                ("$body", WatchProtocol.Encode(newAuthor with { Revision = 1 })));

        // 仅修正旧版本预置昵称的已知错字，不覆盖用户自定义名称。
        if (Scalar("SELECT body FROM authors WHERE account=$account", transaction,
            ("$account", "8632046745")) is string oldBody)
        {
            var old = WatchProtocol.Decode<Author>(oldBody);
            if (old.Name == "逹人知命")
                Execute("UPDATE authors SET body=$body WHERE account=$account", transaction,
                    ("$account", old.Account),
                    ("$body", WatchProtocol.Encode(old with { Name = "达人知命", Revision = old.Revision + 1 })));
        }

        Execute("PRAGMA user_version=6;", transaction);
        transaction.Commit();
    }

    internal Author[] LeadAuthors() => Authors().Where(author => author.Active && !author.Archived).ToArray();
    internal FeedCoverage? LeadCoverage(string account) => ReadLeadCoverage(account);

    private FeedCoverage? ReadLeadCoverage(string account, SqliteTransaction? transaction = null)
    {
        using var command = Command("""
            SELECT state,observed,matches,etag,last_modified FROM lead_coverage WHERE account=$account
            """, transaction, ("$account", account));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;

        var observed = row.GetInt64(1);
        var matches = row.GetInt32(2);
        DateTimeOffset? lastModified = null;
        if (!row.IsDBNull(4) && DateTimeOffset.TryParse(row.GetString(4), CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var parsed))
            lastModified = parsed;
        return new(account, row.GetString(0), observed > 0 ? BeijingTime(observed) : null,
            matches < 0 ? null : matches, row.IsDBNull(3) ? null : row.GetString(3), lastModified);
    }

    internal (int Scanned, int Added, int Failed) SaveLeadScans(
        AuthorFeedScan[] scans, DateTimeOffset observed, CancellationToken token)
    {
        var activeAuthors = LeadAuthors().ToDictionary(author => author.Account, StringComparer.Ordinal);
        var at = observed.ToUnixTimeSeconds();
        using var transaction = db.BeginTransaction();
        var added = 0;
        var failed = 0;
        var scanned = 0;

        foreach (var scan in scans)
        {
            token.ThrowIfCancellationRequested();
            if (!activeAuthors.TryGetValue(scan.Account, out var author) ||
                !string.Equals(author.FeedUrl, scan.Url, StringComparison.Ordinal))
                continue;

            scanned++;
            var links = new Dictionary<string, FeedItem>(StringComparer.Ordinal);
            if (scan.Result.Status == FeedFetchStatus.Updated && scan.Result.Feed is { } feed)
            {
                foreach (var item in feed.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (item.Url is null) continue;
                    try
                    {
                        // Google Alerts 可能把原帖包在 google.com/url 跳转地址中；只接受还原后属于当前作者的雪球原帖。
                        var url = WatchInput.AlertSourceLink(item.Url.AbsoluteUri, scan.Account);
                        links.TryAdd(url, item);
                    }
                    catch (Exception error) when (error is ArgumentException or FormatException)
                    {
                        // 忽略非雪球原帖、其他作者的文章和格式不完整的条目。
                    }
                }
            }

            foreach (var (url, item) in links)
            {
                var title = PlainText(item.Title, 240);
                var summary = PlainText(item.Summary, 1200);
                var body = PlainText(item.Content, 3000);
                var feedAuthor = PlainText(item.Author, 80);
                var itemId = PlainText(item.Id, 300);
                var published = item.PublishedAt?.ToUnixTimeSeconds();
                var existed = Scalar("SELECT 1 FROM post_leads WHERE account=$account AND url=$url",
                    transaction, ("$account", scan.Account), ("$url", url)) is not null;
                using var insert = Command("""
                    INSERT INTO post_leads(account,url,first_seen,title,summary,content,published,feed_author,feed_item_id)
                    VALUES($account,$url,$observed,$title,$summary,$content,$published,$author,$itemId)
                    ON CONFLICT(account,url) DO UPDATE SET
                        title=CASE WHEN excluded.title<>'' THEN excluded.title ELSE post_leads.title END,
                        summary=CASE WHEN length(excluded.summary)>length(post_leads.summary)
                            THEN excluded.summary ELSE post_leads.summary END,
                        content=CASE WHEN length(excluded.content)>length(post_leads.content)
                            THEN excluded.content ELSE post_leads.content END,
                        published=COALESCE(excluded.published,post_leads.published),
                        feed_author=COALESCE(NULLIF(excluded.feed_author,''),post_leads.feed_author),
                        feed_item_id=CASE WHEN excluded.feed_item_id<>'' THEN excluded.feed_item_id ELSE post_leads.feed_item_id END
                    """, transaction,
                    ("$account", scan.Account), ("$url", url), ("$observed", at),
                    ("$title", title), ("$summary", summary), ("$content", body),
                    ("$published", (object?)published ?? DBNull.Value),
                    ("$author", feedAuthor.Length > 0 ? (object)feedAuthor : DBNull.Value),
                    ("$itemId", itemId));
                insert.ExecuteNonQuery();
                if (!existed) added++;
            }

            var previous = ReadLeadCoverage(scan.Account, transaction);
            var state = CoverageState(scan.Result.Status);
            if (state is not ("Read" or "NotModified")) failed++;
            var matches = scan.Result.Status == FeedFetchStatus.Updated ? links.Count : -1;
            var etag = scan.Result.ETag ?? previous?.ETag;
            var modified = scan.Result.LastModified ?? previous?.LastModified;
            Execute("""
                INSERT INTO lead_coverage(account,state,observed,matches,etag,last_modified)
                VALUES($account,$state,$observed,$matches,$etag,$modified)
                ON CONFLICT(account) DO UPDATE SET state=excluded.state,observed=excluded.observed,
                    matches=excluded.matches,etag=excluded.etag,last_modified=excluded.last_modified
                """, transaction,
                ("$account", scan.Account), ("$state", state), ("$observed", at), ("$matches", matches),
                ("$etag", (object?)etag ?? DBNull.Value),
                ("$modified", (object?)modified?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value));
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return (scanned, added, failed);
    }

    private static string PlainText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var withoutActiveContent = Regex.Replace(value, @"<(script|style)\b[^>]*>.*?</\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var withoutTags = Regex.Replace(withoutActiveContent, "<[^>]+>", " ",
            RegexOptions.CultureInvariant);
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var normalized = Regex.Replace(decoded, @"\s+", " ").Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }

    private static string CoverageState(FeedFetchStatus status) => status switch
    {
        FeedFetchStatus.Updated => "Read",
        FeedFetchStatus.NotModified => "NotModified",
        FeedFetchStatus.RateLimited => "RateLimited",
        FeedFetchStatus.AccessDenied => "AccessRequired",
        FeedFetchStatus.NotFound or FeedFetchStatus.Unavailable => "FeedUnavailable",
        FeedFetchStatus.RedirectRejected => "RedirectRejected",
        FeedFetchStatus.TooLarge => "TooLarge",
        FeedFetchStatus.InvalidFeed => "InvalidFeed",
        FeedFetchStatus.TimedOut => "TimedOut",
        _ => "FeedUnavailable"
    };

    private LeadPage ReadLeads(ReviewFilter filter)
    {
        var authors = Authors();
        var coverage = new Dictionary<string, FeedCoverage>(StringComparer.Ordinal);
        using (var command = Command(
            "SELECT account,state,observed,matches,etag,last_modified FROM lead_coverage", null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var account = reader.GetString(0);
                var observed = reader.GetInt64(2);
                var count = reader.GetInt32(3);
                DateTimeOffset? modified = null;
                if (!reader.IsDBNull(5) && DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var parsed))
                    modified = parsed;
                coverage.Add(account, new(account, reader.GetString(1),
                    observed > 0 ? BeijingTime(observed) : null, count < 0 ? null : count,
                    reader.IsDBNull(4) ? null : reader.GetString(4), modified));
            }
        }

        const string condition = "WHERE $a='' OR account=$a";
        var total = Convert.ToInt32(Scalar("SELECT count(*) FROM post_leads " + condition, null, ("$a", filter.Account!)));
        using var leads = Command("""
            SELECT account,url,first_seen,title,summary,content,published,feed_author,feed_item_id
            FROM post_leads
            """ + condition + """
            ORDER BY first_seen DESC,account,url LIMIT $limit OFFSET $offset
            """, null, ("$a", filter.Account!), ("$limit", WatchProtocol.LeadsPerPage), ("$offset", filter.Offset));
        using var rows = leads.ExecuteReader();
        var items = new List<PostLead>();
        while (rows.Read())
        {
            DateTimeOffset? published = rows.IsDBNull(6) ? null : BeijingTime(rows.GetInt64(6));
            items.Add(new(rows.GetString(0), rows.GetString(1), BeijingTime(rows.GetInt64(2)),
                rows.GetString(3), rows.GetString(4), rows.GetString(5), published,
                rows.IsDBNull(7) ? null : rows.GetString(7), rows.GetString(8)));
        }
        rows.Close();

        var active = authors.Where(author => author.Active && !author.Archived).ToArray();
        var configured = active.Count(author => !string.IsNullOrWhiteSpace(author.FeedUrl));
        var unconfigured = active.Length - configured;
        var coverageRows = authors.Select(author => coverage.GetValueOrDefault(author.Account) ??
            new FeedCoverage(author.Account,
                author.Active && !author.Archived && string.IsNullOrWhiteSpace(author.FeedUrl)
                    ? "NotConfigured" : "NeverChecked", null, null)).ToArray();
        return new(configured > 0, authors, coverageRows, items.ToArray(), total, configured, unconfigured);
    }

    private static DateTimeOffset BeijingTime(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(TimeSpan.FromHours(8));
}
