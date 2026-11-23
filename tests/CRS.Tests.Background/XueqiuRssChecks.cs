using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Plugins.XueqiuWatch.Jobs;
using CRS.Worker.Abstractions;
using Microsoft.Data.Sqlite;

internal static class XueqiuRssChecks
{
    private const string FeedUrl = "https://rsshub.app/xueqiu/user/1556808774";

    internal static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var root = Path.Combine(Path.GetTempPath(), "CRS.XueqiuRss", Guid.NewGuid().ToString("N"));
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            SeedVersionOne(root, key);
            await using var session = new XueqiuBackgroundEntry().CreateSession(new Context(root, key), new FeedTransport());
            await session.StartAsync(token);
            var data = (IBackgroundPrivateDataSession)session;
            async Task<WatchReply> Ask(WatchRequest request) =>
                WatchProtocol.Decode<WatchReply>(await data.AccessPrivateDataAsync(WatchProtocol.Encode(request), token));
            Task<BackgroundJobResult> Run() => session.ExecuteAsync(WatchProtocol.LeadJobId, token);
            Task<WatchReply> SaveAuthor(Author author) =>
                Ask(new("author-save", Author: author, ChangeId: Guid.NewGuid()));

            var initial = (await Ask(new("authors"))).Authors!;
            Check(initial.Length == 8 && initial[0].Note == "旧库保留" && (await Run()).Status == "Rejected",
                "旧版作者资料迁移，未配置来源时不执行抓取");

            var invalid = await SaveAuthor(initial[0] with { FeedUrl = "https://www.google.com@localhost/alerts/feeds/1/2" });
            Check(invalid.Error is not null, "拒绝包含凭据和本机地址的 Feed URL");

            Check((await SaveAuthor(initial[0] with { FeedUrl = FeedUrl })).Error is null,
                "作者可独立配置 RSS 地址");
            var paused = await SaveAuthor(initial[1] with
            {
                Active = false, FeedUrl = "https://rsshub.app/xueqiu/user/8790885129"
            });
            Check(paused.Error is null, "暂停作者仍可保留来源配置");

            var first = await Run();
            var leads = (await Ask(new("leads"))).Leads!;
            Check(first.Status == "Succeeded" &&
                leads.Rows is [{ Account: "1556808774", Url: "https://xueqiu.com/1556808774/111" }] &&
                leads.Coverage.Single(item => item.Account == initial[0].Account).State == "Read" &&
                leads.Coverage.Single(item => item.Account == initial[1].Account).State == "NeverChecked",
                "读取 Atom，只接受作者 ID 匹配的雪球原帖并跳过暂停作者");

            Check((await Run()).Status == "Succeeded" &&
                (await Ask(new("leads"))).Leads is { Total: 1 } &&
                (await Ask(new("leads"))).Leads!.Coverage.Single(item => item.Account == initial[0].Account).State == "NotModified",
                "ETag/304 条件请求不重复写入文章");

            var limited = await Run();
            Check(limited is { Status: "RetryDeferred" } &&
                limited.RetryAfter is { } retryAfter && retryAfter >= TimeSpan.FromMinutes(3) &&
                (await Ask(new("leads"))).Leads!.Coverage.Single(item => item.Account == initial[0].Account).State == "RateLimited",
                "限流保留历史线索并记录来源状态");

            Check((await Run()).Status == "Failed" &&
                (await Ask(new("leads"))).Leads!.Coverage.Single(item => item.Account == initial[0].Account).State == "InvalidFeed",
                "拒绝包含 DTD 的恶意 XML Feed");

            var currentAuthors = (await Ask(new("authors"))).Authors!;
            var cleared = await SaveAuthor(currentAuthors.Single(item => item.Account == initial[0].Account) with { FeedUrl = "" });
            var after = (await Ask(new("leads"))).Leads!;
            Check(cleared.Error is null && !after.Configured && after.Total == 1 &&
                after.Coverage.Single(item => item.Account == initial[0].Account).State == "NotConfigured",
                "移除作者 RSS 配置后保留历史线索并清除旧覆盖状态");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void SeedVersionOne(string root, byte[] key)
    {
        // 初始化隔离数据库后回退到 v1 结构，验证升级时保留作者和人工资料。
        using (var store = new WatchStore(root, key))
        {
            var author = WatchProtocol.InitialAuthors[0] with { Revision = 1, Note = "旧库保留" };
            var request = new WatchRequest("author-save", Author: author, ChangeId: Guid.NewGuid());
            store.Access(request, WatchProtocol.Encode(request), default);
        }
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "opinions.db"), Password = Convert.ToHexString(key), Pooling = false
        }.ToString());
        db.Open();
        using var ddl = db.CreateCommand();
        ddl.CommandText = "DROP TABLE post_leads; DROP TABLE lead_coverage; PRAGMA user_version=1";
        ddl.ExecuteNonQuery();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS 雪球 RSS：" + message);
    }

    private sealed class Context(string root, byte[] key) : IBackgroundHostContext
    {
        public string PluginId => WatchProtocol.PluginId;
        public string DataDirectory => root;
        public byte[] OpenDataKey() => key.ToArray();
    }

    private sealed class FeedTransport : HttpMessageHandler
    {
        private int attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == FeedUrl,
                "Worker 只请求作者配置的 RSS 地址");
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 2)
            {
                Check(request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"v1\""), "Worker 使用 ETag 条件请求");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            if (attempt == 3)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
                return Task.FromResult(limited);
            }
            var body = attempt == 4
                ? "<!DOCTYPE rss SYSTEM \"file:///unused\"><rss version=\"2.0\"><channel/></rss>"
                : """
                  <feed xmlns="http://www.w3.org/2005/Atom">
                    <title>雪球作者动态</title>
                    <entry><id>post-111</id><title>作者甲的文章</title>
                      <link rel="alternate" href="https://xueqiu.com/1556808774/111"/></entry>
                    <entry><id>post-222</id><title>其他作者的文章</title>
                      <link rel="alternate" href="https://xueqiu.com/8790885129/222"/></entry>
                    <entry><id>post-333</id><title>第三方链接</title>
                      <link rel="alternate" href="https://example.com/1556808774/333"/></entry>
                  </feed>
                  """;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }
}
