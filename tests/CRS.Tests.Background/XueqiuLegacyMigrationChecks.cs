using System.Text.Json;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Plugins.XueqiuWatch.Jobs;
using Microsoft.Data.Sqlite;

internal static class XueqiuLegacyMigrationChecks
{
    public static Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CRS.XueqiuLegacyMigration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "opinions.db");
        var key = new byte[32];
        SQLitePCL.Batteries_V2.Init();

        using (var connection = Open(database, key))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE authors(account TEXT PRIMARY KEY, body TEXT NOT NULL);
                CREATE TABLE google_alerts(account TEXT PRIMARY KEY, body TEXT NOT NULL);
                CREATE TABLE google_alert_connection(id INTEGER PRIMARY KEY, account TEXT NOT NULL);
                CREATE TABLE rss_proxy(id INTEGER PRIMARY KEY, address TEXT NOT NULL);
                CREATE TABLE full_text_coverage(account TEXT PRIMARY KEY, state TEXT NOT NULL, added INTEGER NOT NULL);
                CREATE TABLE lead_coverage(account TEXT PRIMARY KEY, state TEXT NOT NULL, observed INTEGER NOT NULL);
                INSERT INTO authors(account,body) VALUES(
                    '1556808774',
                    '{"account":"1556808774","name":"旧作者","note":"保留备注","active":true,"archived":false,"revision":7,"feedUrl":"https://example.com/old.xml","fullTextFeedUrl":"https://example.com/full.xml","alertState":2,"googleAlertVerifiedAt":"2026-11-25T10:00:00+08:00"}');
                PRAGMA application_id=1481723735;
                PRAGMA user_version=17;
                """;
            command.ExecuteNonQuery();
        }

        using (new WatchStore(directory, key)) { }

        using (var connection = Open(database, key))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Require(Convert.ToInt32(command.ExecuteScalar()) == 18, "旧版数据库升级到 v18");
            command.CommandText = """
                SELECT count(*) FROM sqlite_schema
                WHERE type='table' AND name IN
                    ('google_alerts','google_alert_connection','rss_proxy','full_text_coverage','lead_coverage')
                """;
            Require(Convert.ToInt32(command.ExecuteScalar()) == 0, "旧 Google / RSS 表已清理");
            command.CommandText = "SELECT body FROM authors WHERE account='1556808774'";
            var body = Convert.ToString(command.ExecuteScalar()) ?? throw new InvalidDataException("旧作者资料未保留。");
            var author = WatchProtocol.Decode<Author>(body);
            Require(author.Name == "旧作者" && author.Note == "保留备注" && author.Active && author.Revision == 7,
                "旧作者资料在移除 Feed 字段后完整保留");
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Require(!root.TryGetProperty("feedUrl", out _) && !root.TryGetProperty("fullTextFeedUrl", out _) &&
                !root.TryGetProperty("alertState", out _) && !root.TryGetProperty("googleAlertVerifiedAt", out _),
                "旧作者 JSON 中的 Google / RSS 配置已清除");
        }

        Console.WriteLine("PASS 雪球观点追踪：v17 数据库升级到 v18，保留作者资料并清理 Google / RSS 配置。");
        return Task.CompletedTask;
    }

    private static SqliteConnection Open(string path, byte[] key)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Password = Convert.ToHexString(key), Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Require(bool success, string check)
    {
        if (!success) throw new InvalidOperationException($"雪球数据库迁移验证失败：{check}");
    }
}