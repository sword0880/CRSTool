using System.Text;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    private const string MentionSources = """
        SELECT p.account,p.url,p.title,p.summary,p.content,b.body
        FROM post_leads p
        LEFT JOIN post_bodies b ON b.account=p.account AND b.url=p.url AND b.revision=(
            SELECT MAX(revision) FROM post_bodies WHERE account=p.account AND url=p.url)
        """;

    private const string MentionQuery = """
        FROM lead_stocks m JOIN post_leads p ON p.account=m.account AND p.url=m.url
        LEFT JOIN original_page_checks o ON o.account=m.account AND o.url=m.url
        LEFT JOIN post_bodies b ON b.account=m.account AND b.url=m.url AND b.revision=(
            SELECT MAX(revision) FROM post_bodies WHERE account=m.account AND url=m.url)
        """;

    private const string MentionPublished = "COALESCE(b.published,o.published,p.timeline_published,p.published)";
    private const string MentionDate = "COALESCE(" + MentionPublished + ",p.first_seen)";

    private void InitializeStockMentions()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE lead_stocks(
                account TEXT NOT NULL,url TEXT NOT NULL,market TEXT NOT NULL,code TEXT NOT NULL,
                name TEXT NOT NULL,excerpt TEXT NOT NULL,PRIMARY KEY(account,url,market,code),
                FOREIGN KEY(account,url) REFERENCES post_leads(account,url) ON DELETE CASCADE);
            CREATE INDEX lead_stocks_by_security ON lead_stocks(market,code,account,url);
            """, transaction);
        using (var command = Command(MentionSources, transaction))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) IndexStockMentions(rows, transaction);
        Execute("PRAGMA user_version=16;", transaction);
        transaction.Commit();
    }

    private void RefreshStockMentions(string account, string url, SqliteTransaction transaction)
    {
        using var command = Command(MentionSources + " WHERE p.account=$a AND p.url=$u", transaction,
            ("$a", account), ("$u", url));
        using var row = command.ExecuteReader();
        if (!row.Read()) return;
        Execute("DELETE FROM lead_stocks WHERE account=$a AND url=$u", transaction, ("$a", account), ("$u", url));
        IndexStockMentions(row, transaction);
    }

    private void IndexStockMentions(SqliteDataReader row, SqliteTransaction transaction)
    {
        var account = row.GetString(0);
        var url = row.GetString(1);
        var title = row.GetString(2);
        // 全文优先，以原文证据替换 RSS 或动态片段中的标签。
        var sources = row.IsDBNull(5)
            ? new[] { row.GetString(4), row.GetString(3), title }
            : new[] { Encoding.UTF8.GetString((byte[])row.GetValue(5)), title };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in sources)
        {
            foreach (var (security, start, length) in WatchInput.StockTags(text))
            {
                if (!seen.Add(security.Key)) continue;
                var excerptStart = Math.Max(0, start - 70);
                if (char.IsLowSurrogate(text[excerptStart])) excerptStart--;
                var excerptEnd = Math.Min(text.Length, start + length + 160);
                if (char.IsHighSurrogate(text[excerptEnd - 1])) excerptEnd--;
                var excerpt = text[excerptStart..excerptEnd].Trim();
                Execute("INSERT INTO lead_stocks VALUES($a,$u,$m,$c,$name,$excerpt)", transaction,
                    ("$a", account), ("$u", url), ("$m", security.Market), ("$c", security.Code),
                    ("$name", security.Name), ("$excerpt", excerpt));
            }
        }
    }

    private IEnumerable<StockSummary> MentionSummaries(ReviewFilter filter, CancellationToken token)
    {
        using var command = Command($"""
            SELECT ranked.market,ranked.code,ranked.name,ranked.posted,ranked.mentions,s.body
            FROM (
                SELECT m.market,m.code,m.name,{MentionDate} AS posted,
                    COUNT(*) OVER (PARTITION BY m.market,m.code) AS mentions,
                    ROW_NUMBER() OVER (PARTITION BY m.market,m.code ORDER BY {MentionDate} DESC,m.url) AS position
                {MentionQuery}
                WHERE {MentionDate}>=$since AND ($a='' OR m.account=$a)
                    AND {MarketCondition("m.market")} AND ($c='' OR m.code=$c)
            ) ranked LEFT JOIN securities s ON s.market=ranked.market AND s.code=ranked.code
            WHERE ranked.position=1
            """, null, ("$since", FilterSince(filter.Days)), ("$a", filter.Account!),
            ("$market", filter.Security?.Market ?? filter.Market!), ("$c", filter.Security?.Code ?? ""));
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            var security = rows.IsDBNull(5) ? new Security(rows.GetString(0), rows.GetString(1), rows.GetString(2), "")
                : WatchProtocol.Decode<Security>(rows.GetString(5));
            yield return new(security, 0, 0, 0, 0, BeijingTime(rows.GetInt64(3))) { Mentions = rows.GetInt32(4) };
        }
    }

    private StockMentionPage StockMentions(ReviewFilter filter)
    {
        if (filter.Security is null) return new([], 0);
        var query = $"""
            {MentionQuery}
            WHERE m.market=$m AND m.code=$c AND ($a='' OR m.account=$a) AND {MentionDate}>=$since
            """;
        (string Name, object Value)[] args = [("$m", filter.Security.Market), ("$c", filter.Security.Code),
            ("$a", filter.Account!), ("$since", FilterSince(filter.Days))];
        var total = Convert.ToInt32(Scalar("SELECT COUNT(*) " + query, null, args));
        using var command = Command($"""
            SELECT m.account,m.url,p.first_seen,{MentionPublished},m.excerpt
            {query}
            ORDER BY {MentionDate} DESC,m.account,m.url LIMIT $limit OFFSET $offset
            """, null, [.. args, ("$limit", WatchProtocol.OpinionsPerPage), ("$offset", filter.Offset)]);
        using var rows = command.ExecuteReader();
        var results = new List<StockMention>();
        while (rows.Read())
            results.Add(new(rows.GetString(0), rows.GetString(1), BeijingTime(rows.GetInt64(2)),
                rows.IsDBNull(3) ? null : BeijingTime(rows.GetInt64(3)), rows.GetString(4)));
        return new(results.ToArray(), total);
    }
}
