using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Plugins.XueqiuWatch.Jobs;
using CRS.Worker.Abstractions;
using Microsoft.Data.Sqlite;

internal static class XueqiuRssChecks
{
    private const string FeedUrl = "https://www.google.com/alerts/feeds/123456789/987654321";

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
            Check(initial.Length == 9 && initial[0].Note == "旧库保留" &&
                initial.Single(x => x.Account == "9650668145").Name == "管我财" &&
                initial.Single(x => x.Account == "2681290304").Name == "学知利行" &&
                initial.Single(x => x.Account == "8632046745").Name == "达人知命" &&
                initial.Single(x => x.Account == "8799248060").Name == "股市小民a" &&
                (await Run()).Status == "Rejected",
                "旧库迁移保留用户资料，并补齐四位大V观察名单");

            Check(WatchInput.GoogleAlertQuery("9650668145") == "site:xueqiu.com/9650668145",
                "按作者数字账号生成独立 Google Alerts 搜索条件");

            var mobile = new Security("港股", "00941", "中国移动", "电信");
            var stockHints = WatchInput.Candidates("看好中国移动，现金流稳定。", [mobile]);
            var directionHints = WatchInput.DirectionPhrases("看好中国移动，暂时观望其他标的。");
            Check(stockHints.Any(item => item.Key == mobile.Key) &&
                directionHints.Contains("看好") && directionHints.Contains("观望"),
                "本地规则可从 RSS 文章文字识别股票候选和多空措辞");
            Check(WatchInput.AlertSourceLink(
                    "https://www.google.com/url?rct=j&sa=t&url=https%3A%2F%2Fxueqiu.com%2F9650668145%2F111&ct=ga",
                    "9650668145") == "https://xueqiu.com/9650668145/111",
                "安全还原 Google Alerts 跳转链接为雪球原帖");
            Reject(() => WatchInput.AlertSourceLink(
                "https://www.google.com/url?url=https%3A%2F%2Fevil.example%2F9650668145%2F111", "9650668145"),
                "拒绝 Google Alerts 指向非雪球站点的链接");
            Reject(() => WatchInput.AlertSourceLink(
                "https://www.google.com/url?url=https%3A%2F%2Fxueqiu.com%2F8790885129%2F111", "9650668145"),
                "拒绝其他作者的雪球文章");
            Reject(() => WatchInput.AlertSourceLink(
                "https://www.google.com/alerts?url=https%3A%2F%2Fxueqiu.com%2F9650668145%2F111", "9650668145"),
                "拒绝非标准 Google 跳转路径");

            var invalid = await SaveAuthor(initial[0] with { FeedUrl = "https://www.google.com@localhost/alerts/feeds/1/2" });
            Check(invalid.Error is not null, "拒绝包含凭据和本机地址的 Feed URL");

            Check((await SaveAuthor(initial.Single(x => x.Account == "9650668145") with { FeedUrl = FeedUrl })).Error is null,
                "作者可独立保存 Google Alerts RSS 地址");
            var paused = await SaveAuthor(initial.Single(x => x.Account == "2681290304") with
            {
                Active = false, FeedUrl = "https://www.google.com/alerts/feeds/paused/author"
            });
            Check(paused.Error is null, "暂停作者仍可保留来源配置");

            var first = await Run();
            var leads = (await Ask(new("leads"))).Leads!;
            Check(first.Status == "Succeeded" &&
                leads.Rows is [{ Account: "9650668145", Url: "https://xueqiu.com/9650668145/111" }] &&
                leads.Coverage.Single(item => item.Account == "9650668145").State == "Read" &&
                leads.Coverage.Single(item => item.Account == "2681290304").State == "NeverChecked",
                "读取 Google Alerts RSS，只接受作者归属匹配的雪球原帖并跳过暂停作者");

            var article = leads.Rows.Single();
            Check(article.Title == "管我财谈中国移动" &&
                article.Summary.Contains("现金流稳定", StringComparison.Ordinal) &&
                article.Content.Contains("AI 算力", StringComparison.Ordinal) &&
                article.PublishedAt is not null && article.FeedAuthor == "管我财",
                "保留 RSS 标题、摘要、正文、发布时间和来源作者，供投资研究插件解析");

            Check((await Run()).Status == "Succeeded" &&
                (await Ask(new("leads"))).Leads is { Total: 1 } &&
                (await Ask(new("leads"))).Leads!.Coverage.Single(item => item.Account == "9650668145").State == "NotModified",
                "ETag/304 条件请求不重复写入文章");

            var limited = await Run();
            Check(limited is { Status: "RetryDeferred" } &&
                limited.RetryAfter is { } retryAfter && retryAfter >= TimeSpan.FromMinutes(3) &&
                (await Ask(new("leads"))).Leads!.Coverage.Single(item => item.Account == "9650668145").State == "RateLimited",
                "限流保留历史线索并记录来源状态");

            Check((await Run()).Status == "Failed" &&
                (await Ask(new("leads"))).Leads!.Coverage.Single(item => item.Account == "9650668145").State == "InvalidFeed",
                "拒绝包含 DTD 的恶意 XML Feed");

            var currentAuthors = (await Ask(new("authors"))).Authors!;
            var cleared = await SaveAuthor(currentAuthors.Single(item => item.Account == "9650668145") with { FeedUrl = "" });
            var after = (await Ask(new("leads"))).Leads!;
            Check(cleared.Error is null && !after.Configured && after.Total == 1 &&
                after.Coverage.Single(item => item.Account == "9650668145").State == "NotConfigured",
                "移除作者 RSS 配置后保留历史线索并清除旧覆盖状态");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void SeedVersionOne(string root, byte[] key)
    {
        // 初始化隔离数据库后回退到旧 v1 结构，模拟升级时补齐新观察作者和 RSS 元数据字段。
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
        using var update = db.CreateCommand();
        var oldAuthor = WatchProtocol.InitialAuthors.Single(x => x.Account == "8632046745") with { Name = "逹人知命" };
        update.CommandText = "UPDATE authors SET body=$body WHERE account=$account; DELETE FROM authors WHERE account='8799248060';";
        update.Parameters.AddWithValue("$body", WatchProtocol.Encode(oldAuthor with { Revision = 1 }));
        update.Parameters.AddWithValue("$account", oldAuthor.Account);
        update.ExecuteNonQuery();
        using var ddl = db.CreateCommand();
        ddl.CommandText = "DROP TABLE post_leads; DROP TABLE lead_coverage; PRAGMA user_version=1";
        ddl.ExecuteNonQuery();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS 雪球 RSS： " + message);
    }

    private static void Reject(Action work, string message)
    {
        try { work(); }
        catch (ArgumentException) { Check(true, message); return; }
        throw new InvalidOperationException("未拒绝：" + message);
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
                "Worker 只请求作者配置的 Google Alerts RSS 地址");
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
                    <title>Google Alerts - 管我财</title>
                    <entry>
                      <id>alert-111</id><title>管我财谈中国移动</title>
                      <link rel="alternate" href="https://www.google.com/url?rct=j&amp;sa=t&amp;url=https%3A%2F%2Fxueqiu.com%2F9650668145%2F111&amp;ct=ga"/>
                      <summary>看好中国移动，现金流稳定。</summary>
                      <content type="html">&lt;p&gt;看好中国移动，AI 算力投入有望形成新增长点。&lt;/p&gt;</content>
                      <published>2026-11-23T01:00:00Z</published>
                      <author><name>管我财</name></author>
                    </entry>
                    <entry><id>alert-222</id><title>其他作者的文章</title>
                      <link rel="alternate" href="https://xueqiu.com/2681290304/222"/></entry>
                    <entry><id>alert-333</id><title>第三方链接</title>
                      <link rel="alternate" href="https://example.com/9650668145/333"/></entry>
                  </feed>
                  """;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }
}
