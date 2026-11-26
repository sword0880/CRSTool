using System.Globalization;
using System.Text.Json;
using CRS.Common.Text;
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
                if (SaveLead(new(scan.Account, url, observed, item.Title ?? "", item.Summary ?? "", item.Content ?? "",
                    item.PublishedAt, item.Author, item.Id ?? ""), fromTimeline: false, transaction))
                    added++;
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

    private bool SaveLead(PostLead lead, bool fromTimeline, SqliteTransaction transaction)
    {
        var account = lead.Account;
        var url = lead.Url;
        var title = PlainText(lead.Title, 240);
        var summary = PlainText(lead.Summary, 1200);
        var content = PlainText(lead.Content, 3000);
        var published = WatchInput.PostTime(lead.PublishedAt, lead.FirstSeen)?.ToUnixTimeSeconds();
        var existed = Scalar("SELECT 1 FROM post_leads WHERE account=$account AND url=$url",
            transaction, ("$account", account), ("$url", url)) is not null;
        Execute("""
            INSERT INTO post_leads(account,url,first_seen,title,summary,content,published,feed_author,feed_item_id,timeline_published)
            VALUES($account,$url,$observed,$title,$summary,$content,$published,NULLIF($author,''),$itemId,$timeline)
            ON CONFLICT(account,url) DO UPDATE SET
                title=CASE WHEN excluded.title<>'' AND (excluded.timeline_published IS NOT NULL OR
                    post_leads.timeline_published IS NULL OR post_leads.title='') THEN excluded.title ELSE post_leads.title END,
                summary=CASE WHEN length(excluded.summary)>length(post_leads.summary)
                    THEN excluded.summary ELSE post_leads.summary END,
                content=CASE WHEN length(excluded.content)>length(post_leads.content)
                    THEN excluded.content ELSE post_leads.content END,
                published=COALESCE(excluded.published,post_leads.published),
                feed_author=COALESCE(excluded.feed_author,post_leads.feed_author),
                feed_item_id=CASE WHEN excluded.feed_item_id<>'' THEN excluded.feed_item_id ELSE post_leads.feed_item_id END,
                timeline_published=COALESCE(excluded.timeline_published,post_leads.timeline_published)
            """, transaction, ("$account", account), ("$url", url), ("$observed", lead.FirstSeen.ToUnixTimeSeconds()),
            ("$title", title), ("$summary", summary), ("$content", content),
            ("$published", !fromTimeline ? (object?)published ?? DBNull.Value : DBNull.Value),
            ("$author", PlainText(lead.FeedAuthor, 80)), ("$itemId", PlainText(lead.FeedItemId, 300)),
            ("$timeline", fromTimeline ? (object?)published ?? DBNull.Value : DBNull.Value));
        return !existed;
    }

    private static string PlainText(string? value, int maximum)
    {
        var text = HtmlText.ToPlainText(value);
        return LimitText(text, maximum);
    }

    private static string LimitText(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        if (maximum > 0 && char.IsHighSurrogate(text[maximum - 1])) maximum--;
        return text[..maximum];
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
        FeedFetchStatus.ProxyUnavailable => "ProxyUnavailable",
        _ => "FeedUnavailable"
    };

    private LeadPage ReadLeads(ReviewFilter filter)
    {
        var authors = AuthorsForDisplay();
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

        const string condition = "WHERE $a='' OR p.account=$a";
        var total = Convert.ToInt32(Scalar("SELECT count(*) FROM post_leads p " + condition, null, ("$a", filter.Account!)));
        var query = """
            SELECT p.account,p.url,p.first_seen,p.title,p.summary,p.content,
                COALESCE(b.published,s.published,p.timeline_published,p.published),
                p.feed_author,p.feed_item_id,COALESCE(b.revision,0),COALESCE(b.origin,0),s.state,
                COALESCE(s.published,p.timeline_published),s.http_status
            FROM post_leads p
            LEFT JOIN original_page_checks s ON s.account=p.account AND s.url=p.url
            LEFT JOIN post_bodies b ON b.account=p.account AND b.url=p.url AND b.revision=(
                SELECT MAX(latest.revision) FROM post_bodies latest WHERE latest.account=p.account AND latest.url=p.url
            )
            """ + " " + condition +
            " ORDER BY p.first_seen DESC,COALESCE(p.timeline_published,p.published) DESC,p.account,p.url LIMIT $limit OFFSET $offset";
        using var leads = Command(query, null, ("$a", filter.Account!),
            ("$limit", WatchProtocol.LeadsPerPage), ("$offset", filter.Offset));
        using var rows = leads.ExecuteReader();
        var items = new List<PostLead>();
        var now = WatchProtocol.BeijingNow;
        while (rows.Read())
        {
            var published = rows.IsDBNull(6) ? null : WatchInput.PostTime(BeijingTime(rows.GetInt64(6)), now);
            items.Add(new(rows.GetString(0), rows.GetString(1), BeijingTime(rows.GetInt64(2)),
                rows.GetString(3), rows.GetString(4), rows.GetString(5), published,
                rows.IsDBNull(7) ? null : rows.GetString(7), rows.GetString(8), rows.GetInt64(9),
                (PostBodySource)rows.GetInt32(10))
            {
                OriginalPageState = rows.IsDBNull(11) ? null : rows.GetString(11),
                OriginalPageTimeVerified = !rows.IsDBNull(12),
                OriginalPageHttpStatus = rows.IsDBNull(13) ? null : rows.GetInt32(13)
            });
        }
        rows.Close();

        var active = authors.Where(author => author.Active && !author.Archived).ToArray();
        var configured = active.Count(author => !string.IsNullOrWhiteSpace(author.FeedUrl));
        var unconfigured = active.Length - configured;
        var fullTextCoverage = ReadFullTextCoverage();
        var timelineCoverage = ReadTimelineCoverage();
        var backfill = PendingBackfillAccounts();
        var coverageRows = authors.Select(author =>
        {
            var row = coverage.GetValueOrDefault(author.Account) ?? new FeedCoverage(author.Account,
                author.Active && !author.Archived && string.IsNullOrWhiteSpace(author.FeedUrl)
                    ? "NotConfigured" : "NeverChecked", null, null);
            row = row with { Timeline = timelineCoverage.GetValueOrDefault(author.Account),
                BackfillPending = backfill.Contains(author.Account) };
            if (author.FullTextFeedUrl.Length == 0) return row;
            return fullTextCoverage.TryGetValue(author.Account, out var fullText)
                ? row with { FullTextState = fullText.State, FullTextCount = fullText.Added }
                : row with { FullTextState = "NeverChecked" };
        }).ToArray();
        return FitLeadPreview(new(configured > 0, authors, coverageRows, items.ToArray(), total, configured, unconfigured)
            { TimelineSupported = true, BackfillSupported = true });
    }

    private static LeadPage FitLeadPreview(LeadPage page)
    {
        // 线索 JSON 在 Runner 和桌面管道中又作为字符串转义，预留各自响应信封的空间。
        if (Fits(page)) return page;
        var compact = Preview(page, 420);
        if (Fits(compact)) return compact;
        compact = Preview(page, 160);
        if (Fits(compact)) return compact;
        // 大观察名单的完整配置仍由 authors 请求提供，线索页保留筛选和状态所需字段。
        compact = compact with
        {
            Authors = compact.Authors.Select(author => author with { Note = "", FeedUrl = "", FullTextFeedUrl = "" }).ToArray(),
            Coverage = compact.Coverage.Select(coverage => coverage with { ETag = null }).ToArray(),
            Rows = compact.Rows.Select(post => post with { FeedAuthor = null, FeedItemId = "" }).ToArray()
        };
        return Fits(compact) ? compact : Preview(compact, 0);

        static bool Fits(LeadPage result) => JsonEncodedText.Encode(WatchProtocol.Encode(new WatchReply { Leads = result }))
            .EncodedUtf8Bytes.Length <= 120 * 1024;

        static LeadPage Preview(LeadPage result, int limit) => result with
        {
            Rows = result.Rows.Select(post => post with
            {
                Summary = LimitText(post.Summary, limit),
                Content = LimitText(post.Content, limit)
            }).ToArray()
        };
    }

    private static DateTimeOffset BeijingTime(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(TimeSpan.FromHours(8));
}
