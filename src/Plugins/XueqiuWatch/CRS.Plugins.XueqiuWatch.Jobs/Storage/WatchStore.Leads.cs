using System.Text.Json;
using CRS.Common.Text;
using CRS.Plugins.XueqiuWatch.Core;
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

    private bool SaveLead(PostLead lead, SqliteTransaction transaction)
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
            INSERT INTO post_leads(account,url,first_seen,title,summary,content,published,timeline_published)
            VALUES($account,$url,$observed,$title,$summary,$content,$published,$timeline)
            ON CONFLICT(account,url) DO UPDATE SET
                title=CASE WHEN excluded.title<>'' THEN excluded.title ELSE post_leads.title END,
                summary=CASE WHEN length(excluded.summary)>length(post_leads.summary)
                    THEN excluded.summary ELSE post_leads.summary END,
                content=CASE WHEN length(excluded.content)>length(post_leads.content)
                    THEN excluded.content ELSE post_leads.content END,
                published=COALESCE(excluded.published,post_leads.published),
                timeline_published=COALESCE(excluded.timeline_published,post_leads.timeline_published)
            """, transaction, ("$account", account), ("$url", url), ("$observed", lead.FirstSeen.ToUnixTimeSeconds()),
            ("$title", title), ("$summary", summary), ("$content", content),
            ("$published", (object?)published ?? DBNull.Value),
            ("$timeline", (object?)published ?? DBNull.Value));
        RefreshStockMentions(account, url, transaction);
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

    private LeadPage ReadLeads(ReviewFilter filter)
    {
        var authors = Authors();
        var timelineCoverage = ReadTimelineCoverage();
        var backfill = PendingBackfillAccounts();
        const string condition = "WHERE $a='' OR p.account=$a";
        var total = Convert.ToInt32(Scalar("SELECT count(*) FROM post_leads p " + condition, null, ("$a", filter.Account!)));
        var query = """
            SELECT p.account,p.url,p.first_seen,p.title,p.summary,p.content,
                COALESCE(b.published,s.published,p.timeline_published,p.published),
                COALESCE(b.revision,0),COALESCE(b.origin,0),s.state,
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
                rows.GetInt64(7), (PostBodySource)rows.GetInt32(8))
            {
                OriginalPageState = rows.IsDBNull(9) ? null : rows.GetString(9),
                OriginalPageTimeVerified = !rows.IsDBNull(10),
                OriginalPageHttpStatus = rows.IsDBNull(11) ? null : rows.GetInt32(11)
            });
        }
        rows.Close();

        var coverageRows = authors.Select(author => new AuthorCoverage(author.Account,
            timelineCoverage.GetValueOrDefault(author.Account), backfill.Contains(author.Account))).ToArray();
        return FitLeadPreview(new(authors, coverageRows, items.ToArray(), total)
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
        compact = compact with
        {
            Authors = compact.Authors.Select(author => author with { Note = "" }).ToArray()
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
