using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed record OriginalPageLead(string Account, string Url, long FirstSeen, long LastChecked, bool Backfill = false);
internal sealed record OriginalPageRead(string State, string? Text = null, DateTimeOffset? Published = null,
    TimeSpan? RetryAfter = null, int? HttpStatus = null);

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

    private void InitializeOriginalPageDetails()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            ALTER TABLE original_page_checks ADD COLUMN published INTEGER;
            ALTER TABLE original_page_checks ADD COLUMN http_status INTEGER;
            UPDATE original_page_checks SET retry=0 WHERE state='Unavailable';
            PRAGMA user_version=13;
            """, transaction);
        transaction.Commit();
    }

    internal OriginalPageLead[] OriginalPageQueue(DateTimeOffset now, int limit)
    {
        // 排入启用作者尚未存有正文、且已到重试时间的线索。优先核对从未读取的近期动态。
        var posts = new List<OriginalPageLead>();
        foreach (var author in LeadAuthors())
        {
            using var command = Command("""
                SELECT p.url,COALESCE(p.timeline_published,p.first_seen),COALESCE(s.checked,0),
                    CASE WHEN
                """ + " " + BackfillPost + """
                     AND (s.checked IS NULL OR s.state='RateLimited') THEN 1 ELSE 0 END AS pending_backfill
                FROM post_leads p LEFT JOIN author_backfill f ON f.account=p.account
                LEFT JOIN original_page_checks s ON s.account=p.account AND s.url=p.url
                WHERE p.account=$account AND COALESCE(s.retry,0)<=$now
                AND NOT EXISTS(SELECT 1 FROM post_bodies b WHERE b.account=p.account AND b.url=p.url)
                ORDER BY pending_backfill DESC,COALESCE(s.checked,0),COALESCE(p.timeline_published,p.first_seen) DESC,p.url LIMIT $limit
                """, null, ("$account", author.Account), ("$now", now.ToUnixTimeSeconds()), ("$limit", limit));
            using var rows = command.ExecuteReader();
            while (rows.Read()) posts.Add(new(author.Account, rows.GetString(0), rows.GetInt64(1), rows.GetInt64(2), rows.GetBoolean(3)));
        }
        return posts.OrderByDescending(post => post.Backfill).ThenBy(post => post.LastChecked).ThenByDescending(post => post.FirstSeen)
            .ThenBy(post => post.Url, StringComparer.Ordinal).Take(limit).ToArray();
    }

    internal (int Status, DateTimeOffset Until)? ApiPause(DateTimeOffset now)
    {
        using var command = Command("""
            SELECT http_status,retry FROM original_page_checks WHERE http_status IN (429,567) AND retry>$now
            UNION ALL
            SELECT http_status,retry FROM author_timeline WHERE http_status IN (429,567) AND retry>$now
            ORDER BY retry DESC LIMIT 1
            """, null, ("$now", now.ToUnixTimeSeconds()));
        using var row = command.ExecuteReader();
        return row.Read() ? (row.GetInt32(0), BeijingTime(row.GetInt64(1))) : null;
    }

    internal bool SaveOriginalPage(OriginalPageLead post, OriginalPageRead result, DateTimeOffset now, CancellationToken token)
    {
        // 网络读取期间作者可能被暂停或取消订阅，写入前重新检查其状态。
        token.ThrowIfCancellationRequested();
        if (!LeadAuthors().Any(author => author.Account == post.Account)) return false;
        using var transaction = db.BeginTransaction();
        var state = result.State;
        var originalTime = WatchInput.PostTime(result.Published, now);
        var saved = state == "FullText" && result.Text is { } text &&
            originalTime is { } published &&
            TrySaveAutomaticBody(post.Account, post.Url, published, text, PostBodySource.OriginalApi, transaction);
        if (!saved && state == "FullText") state = "BodyUnavailable";
        // 429 和 567 暂停原帖 API 请求，按任务退避时间恢复。
        var delay = result.RetryAfter ?? TimeSpan.FromHours(result.HttpStatus == 567 ? 6 :
            state is "BodyUnavailable" or "AccessRestricted" ? 24 : 2);
        Execute("""
            INSERT INTO original_page_checks(account,url,state,checked,retry,published,http_status)
            VALUES($a,$u,$s,$c,$retry,$published,$http)
            ON CONFLICT(account,url) DO UPDATE SET state=excluded.state,checked=excluded.checked,retry=excluded.retry,
                published=COALESCE(excluded.published,original_page_checks.published),http_status=excluded.http_status
            """, transaction, ("$a", post.Account), ("$u", post.Url), ("$s", state), ("$c", now.ToUnixTimeSeconds()),
            ("$retry", now.Add(delay).ToUnixTimeSeconds()),
            ("$published", (object?)originalTime?.ToUnixTimeSeconds() ?? DBNull.Value),
            ("$http", (object?)result.HttpStatus ?? DBNull.Value));
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return saved;
    }
}
