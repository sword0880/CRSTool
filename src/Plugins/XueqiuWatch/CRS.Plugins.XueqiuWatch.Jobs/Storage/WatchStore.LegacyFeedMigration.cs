using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    // 仅供旧数据库顺序升级使用；新版本不再提供 Google Alerts 或 RSS 订阅功能。
    private void InitializeGoogleAlerts()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE google_alerts(account TEXT PRIMARY KEY REFERENCES authors(account), body TEXT NOT NULL,
                google_account TEXT NOT NULL, remote_id TEXT NOT NULL);
            CREATE UNIQUE INDEX google_alert_owner ON google_alerts(google_account, remote_id) WHERE remote_id <> '';
            PRAGMA user_version=7;
            """, transaction);
        transaction.Commit();
    }

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

    private void InitializeGoogleConnection()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE google_alert_connection(id INTEGER PRIMARY KEY CHECK(id=1), account TEXT NOT NULL);
            INSERT INTO google_alert_connection VALUES(1,'');
            PRAGMA user_version=11;
            """, transaction);
        transaction.Commit();
    }

    private void InitializeRemoveLegacyFeeds()
    {
        using var transaction = db.BeginTransaction();
        foreach (var author in Authors(transaction))
        {
            // 重新保存作者资料，移除旧版 RSS 地址和 Google 账号同步字段。
            var clean = new Author(author.Account, author.Name, author.Note, author.Active, author.Archived, author.Revision);
            Execute("UPDATE authors SET body=$body WHERE account=$account", transaction,
                ("$account", clean.Account), ("$body", WatchProtocol.Encode(clean)));
        }

        Execute("""
            DROP TABLE IF EXISTS google_alerts;
            DROP TABLE IF EXISTS google_alert_connection;
            DROP TABLE IF EXISTS rss_proxy;
            DROP TABLE IF EXISTS full_text_coverage;
            DROP TABLE IF EXISTS lead_coverage;
            PRAGMA user_version=18;
            """, transaction);
        transaction.Commit();
    }
}