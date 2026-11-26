using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed record AuthorBackfill(DateTimeOffset? WindowEnd, int Page, string[] Anchors, bool Discovered = false);

internal sealed partial class WatchStore
{
    private void InitializeBackfill()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE author_backfill(account TEXT PRIMARY KEY REFERENCES authors(account),
                window_end INTEGER, page INTEGER NOT NULL DEFAULT 0, anchors TEXT NOT NULL DEFAULT '',
                discovered INTEGER NOT NULL DEFAULT 0);
            PRAGMA user_version=15;
            """, transaction);
        transaction.Commit();
    }

    private void EnqueueBackfill(Author author, SqliteTransaction transaction) =>
        Execute("INSERT INTO author_backfill(account,window_end) VALUES($a,$end)", transaction,
            ("$a", author.Account), ("$end", author.Active ? (object)WatchProtocol.BeijingNow.ToUnixTimeSeconds() : DBNull.Value));

    internal AuthorBackfill? PendingBackfill(string account)
    {
        using var command = Command("""
            SELECT window_end,page,anchors FROM author_backfill WHERE account=$a AND discovered=0
            """, null, ("$a", account));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.IsDBNull(0) ? null : BeijingTime(row.GetInt64(0)), row.GetInt32(1),
            row.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries)) : null;
    }

    private void SaveBackfill(string account, AuthorBackfill progress, SqliteTransaction transaction) =>
        Execute("""
            UPDATE author_backfill SET window_end=$end,page=$page,anchors=$anchors,discovered=$finished WHERE account=$a
            """, transaction, ("$a", account), ("$end", progress.WindowEnd!.Value.ToUnixTimeSeconds()),
            ("$page", progress.Page), ("$anchors", string.Join(',', progress.Anchors)), ("$finished", progress.Discovered ? 1 : 0));

    private const int HistorySeconds = WatchProtocol.NewAuthorHistoryDays * 24 * 60 * 60;
    private static readonly string BackfillPost = $"f.window_end IS NOT NULL AND p.timeline_published BETWEEN f.window_end - {HistorySeconds} AND f.window_end";

    private static readonly string UncheckedBackfillBody = BackfillPost + """
         AND (s.checked IS NULL OR s.state='RateLimited')
         AND NOT EXISTS(SELECT 1 FROM post_bodies b WHERE b.account=p.account AND b.url=p.url)
        """;

    internal HashSet<string> PendingBackfillAccounts()
    {
        using var command = Command("""
            SELECT f.account FROM author_backfill f WHERE f.discovered=0 OR EXISTS(
                SELECT 1 FROM post_leads p LEFT JOIN original_page_checks s ON p.account=s.account AND p.url=s.url
                WHERE p.account=f.account AND
            """ + " " + UncheckedBackfillBody + ")", null);
        using var rows = command.ExecuteReader();
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        while (rows.Read())
            accounts.Add(rows.GetString(0));
        return accounts;
    }

    internal int PendingBackfillCount()
    {
        var accounts = PendingBackfillAccounts();
        return LeadAuthors().Count(author => accounts.Contains(author.Account));
    }
}
