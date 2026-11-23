using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    private void InitializeRssProxy()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE rss_proxy(id INTEGER PRIMARY KEY CHECK(id=1), address TEXT NOT NULL);
            INSERT INTO rss_proxy VALUES(1,'');
            PRAGMA user_version=8;
            """, transaction);
        transaction.Commit();
    }

    internal string ReadRssProxy() => (string?)Scalar("SELECT address FROM rss_proxy WHERE id=1") ?? "";

    private void SaveRssProxy(string? address, SqliteTransaction transaction) =>
        Execute("UPDATE rss_proxy SET address=$address WHERE id=1", transaction,
            ("$address", WatchInput.NormalizeRssProxy(address)));
}
