using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    private void InitializeHiddenStocks()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE hidden_stocks(market TEXT NOT NULL,code TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(market,code));
            PRAGMA user_version=17;
            """, transaction);
        transaction.Commit();
    }

    private void SaveStockVisibility(StockVisibilityChange? input, SqliteTransaction transaction)
    {
        if (input?.Security is null) throw new ArgumentException("请先选择股票。");
        var security = WatchInput.CheckSecurity(input.Security);
        if (input.Hidden)
            Execute("""
                INSERT INTO hidden_stocks VALUES($m,$c,$body)
                ON CONFLICT(market,code) DO UPDATE SET body=excluded.body
                """, transaction, ("$m", security.Market), ("$c", security.Code), ("$body", WatchProtocol.Encode(security)));
        else
            Execute("DELETE FROM hidden_stocks WHERE market=$m AND code=$c", transaction,
                ("$m", security.Market), ("$c", security.Code));
    }

    private HashSet<string> HiddenStockKeys()
    {
        using var command = Command("SELECT market,code FROM hidden_stocks", null);
        using var rows = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (rows.Read()) keys.Add($"{rows.GetString(0)}:{rows.GetString(1)}");
        return keys;
    }

    private HiddenStockPage ReadHiddenStocks(int offset)
    {
        var total = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM hidden_stocks"));
        using var command = Command("SELECT body FROM hidden_stocks ORDER BY market,code LIMIT $limit OFFSET $offset", null,
            ("$limit", WatchProtocol.StocksPerPage), ("$offset", offset));
        using var rows = command.ExecuteReader();
        var results = new List<Security>();
        while (rows.Read()) results.Add(WatchProtocol.Decode<Security>(rows.GetString(0)));
        return new(results.ToArray(), total);
    }

    private StockVisibilityPage ReadStockVisibility(ReviewFilter filter, CancellationToken token)
    {
        const string knownStocks = """
            WITH known(market,code) AS (
                SELECT market,code FROM securities
                UNION SELECT market,code FROM lead_stocks
                UNION SELECT market,code FROM hidden_stocks
            )
            """;
        var marketCondition = MarketCondition("k.market");
        var market = filter.Market ?? "";
        var total = Convert.ToInt32(Scalar($"{knownStocks} SELECT COUNT(*) FROM known k WHERE {marketCondition}",
            null, ("$market", market)));
        using var command = Command($"""
            {knownStocks}
            SELECT k.market,k.code,s.body,m.name,h.body FROM known k
            LEFT JOIN securities s ON s.market=k.market AND s.code=k.code
            LEFT JOIN hidden_stocks h ON h.market=k.market AND h.code=k.code
            LEFT JOIN lead_stocks m ON m.rowid=(
                SELECT lead.rowid FROM lead_stocks lead
                JOIN post_leads p ON p.account=lead.account AND p.url=lead.url
                WHERE lead.market=k.market AND lead.code=k.code
                ORDER BY COALESCE(p.timeline_published,p.published,p.first_seen) DESC,lead.rowid DESC LIMIT 1
            )
            WHERE {marketCondition}
            ORDER BY CASE WHEN k.market IN ('沪市','深市','北交所') THEN 0 WHEN k.market='港股' THEN 1 ELSE 2 END,
                k.market,k.code
            LIMIT $limit OFFSET $offset
            """, null, ("$market", market), ("$limit", WatchProtocol.VisibilityStocksPerPage), ("$offset", filter.Offset));
        using var rows = command.ExecuteReader();
        var results = new List<StockVisibilityChange>();
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            var hidden = !rows.IsDBNull(4);
            var security = !rows.IsDBNull(2) ? WatchProtocol.Decode<Security>(rows.GetString(2)) :
                !rows.IsDBNull(3) ? new(rows.GetString(0), rows.GetString(1), rows.GetString(3), "") :
                WatchProtocol.Decode<Security>(rows.GetString(4));
            results.Add(new(security, hidden));
        }
        return new(results.ToArray(), total);
    }
}
