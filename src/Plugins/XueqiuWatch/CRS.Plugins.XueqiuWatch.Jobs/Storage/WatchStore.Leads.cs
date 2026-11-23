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

    internal (string? Url, string? Scope) LeadFeed(SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT url,scope FROM lead_feed WHERE id=1", transaction);
        using var row = command.ExecuteReader();
        return row.Read() ? (row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1)) : (null, null);
    }

    internal Author[] LeadAuthors() => Authors().Where(author => author.Active && !author.Archived).ToArray();

    private LeadSubscription ReadSubscription(SqliteTransaction? transaction = null)
    {
        var (url, scope) = LeadFeed(transaction);
        return new(AuthorScope(Authors(transaction)), scope, url is null ? null : LeadSubscription.Fingerprint(url),
            url is null ? null : LeadSubscription.OwnerOf(url));
    }

    private static string AuthorScope(Author[] authors) => string.Join(',', authors
        .Where(author => author.Active && !author.Archived).Select(author => author.Account).Order(StringComparer.Ordinal));

    private void SyncFeed(WatchRequest request, SqliteTransaction transaction)
    {
        var state = ReadSubscription(transaction);
        if (state.Scope.Length == 0 || request.FeedScope != state.Scope ||
            request.PreviousFeedFingerprint != state.FeedFingerprint)
            throw new ArgumentException("观察名单或订阅刚刚被修改，请重新同步。");
        var url = WatchInput.GoogleAlertFeed(request.FeedUrl);
        if (state.OwnerFingerprint is { } owner && LeadSubscription.OwnerOf(url) != owner)
            throw new ArgumentException("新链接与当前订阅的 Google 账号不一致。");
        WriteFeed(url, state.Scope, transaction);
    }

    private void SaveFeed(string? input, SqliteTransaction transaction)
    {
        if (input is null) throw new ArgumentException("请填写订阅链接，或选择停用订阅。");
        var url = input.Length == 0 ? null : WatchInput.GoogleAlertFeed(input);
        if (url == LeadFeed(transaction).Url) return;
        WriteFeed(url, null, transaction);
    }

    private void WriteFeed(string? url, string? scope, SqliteTransaction transaction)
    {
        if (LeadFeed(transaction) == (url, scope)) return;
        // 保留已发现的线索；不同订阅的检查时间与覆盖结果不能混用。
        Execute("DELETE FROM lead_coverage", transaction);
        if (url is null)
        {
            Execute("DELETE FROM lead_feed WHERE id=1", transaction);
            return;
        }
        Execute("""
            INSERT INTO lead_feed(id,url,scope) VALUES(1,$u,$scope)
            ON CONFLICT(id) DO UPDATE SET url=excluded.url,scope=excluded.scope
            """, transaction, ("$u", url), ("$scope", (object?)scope ?? DBNull.Value));
    }

    internal (int Authors, int Added) SaveLeadScan(LeadFeedResult result, string? scope,
        DateTimeOffset observed, CancellationToken token)
    {
        var coveredAccounts = scope?.Split(',').ToHashSet(StringComparer.Ordinal);
        var authors = LeadAuthors().Where(author => coveredAccounts is null || coveredAccounts.Contains(author.Account)).ToArray();
        var byAccount = result.Links.ToLookup(link => link.Account, StringComparer.Ordinal);
        var at = observed.ToUnixTimeSeconds();
        using var transaction = db.BeginTransaction();
        var added = 0;
        foreach (var author in authors)
        {
            token.ThrowIfCancellationRequested();
            var links = byAccount[author.Account];
            foreach (var link in links)
            {
                using var insert = Command("INSERT OR IGNORE INTO post_leads VALUES($a,$u,$t)", transaction,
                    ("$a", author.Account), ("$u", link.Url), ("$t", at));
                added += insert.ExecuteNonQuery();
            }
            Execute("""
                INSERT INTO lead_coverage VALUES($a,$s,$t,$m)
                ON CONFLICT(account) DO UPDATE SET state=excluded.state,observed=excluded.observed,matches=excluded.matches
                """, transaction, ("$a", author.Account), ("$s", result.State), ("$t", at),
                ("$m", result.State == "Read" ? links.Count() : -1));
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return (authors.Length, added);
    }

    private LeadPage ReadLeads(ReviewFilter filter)
    {
        var authors = Authors();
        var coverage = new Dictionary<string, FeedCoverage>(StringComparer.Ordinal);
        using (var command = Command("SELECT account,state,observed,matches FROM lead_coverage", null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var account = reader.GetString(0);
                var count = reader.GetInt32(3);
                coverage.Add(account, new(account, reader.GetString(1), BeijingTime(reader.GetInt64(2)), count < 0 ? null : count));
            }
        }
        const string condition = "WHERE $a='' OR account=$a";
        var total = Convert.ToInt32(Scalar("SELECT count(*) FROM post_leads " + condition, null, ("$a", filter.Account!)));
        using var leads = Command("SELECT account,url,first_seen FROM post_leads " + condition +
            " ORDER BY first_seen DESC,account,url LIMIT $limit OFFSET $offset", null,
            ("$a", filter.Account!), ("$limit", WatchProtocol.LeadsPerPage), ("$offset", filter.Offset));
        using var rows = leads.ExecuteReader();
        var items = new List<PostLead>();
        while (rows.Read()) items.Add(new(rows.GetString(0), rows.GetString(1), BeijingTime(rows.GetInt64(2))));
        rows.Close();
        var (url, scope) = LeadFeed();
        var wantedScope = AuthorScope(authors);
        return new(url is not null, authors, authors.Select(author =>
            coverage.GetValueOrDefault(author.Account) ?? new(author.Account, "NeverChecked", null, null)).ToArray(),
            items.ToArray(), total, wantedScope.Length > 0 && wantedScope != scope);
    }

    private static DateTimeOffset BeijingTime(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(TimeSpan.FromHours(8));
}
