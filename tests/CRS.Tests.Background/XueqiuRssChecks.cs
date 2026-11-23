using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Plugins.XueqiuWatch.Jobs;
using CRS.Worker.Abstractions;
using Microsoft.Data.Sqlite;

internal static class XueqiuRssChecks
{
    private const string FeedUrl = "https://www.google.com/alerts/feeds/synthetic/test";

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
            Task<WatchReply> Configure(string url) => Ask(new("lead-feed-save", ChangeId: Guid.NewGuid()) { FeedUrl = url });

            var initial = (await Ask(new("leads"))).Leads!;
            Check(initial is { Configured: false, Authors.Length: 8, Total: 0 } &&
                initial.Authors[0].Note == "旧库保留" && (await Run()).Status == "Rejected",
                "旧版资料迁移，缺订阅时直接提示");
            Check((await Configure("https://www.google.com@localhost/alerts/feeds/1/2")).Error is not null,
                "拒绝不受支持的订阅 URL");
            Check((await Configure(FeedUrl)).Error is null && (await Ask(new("leads"))).Leads?.Configured == true,
                "订阅配置可保存在隔离数据库");
            var paused = initial.Authors[1];
            Check((await Ask(new("author-save", Author: paused with { Active = false }, ChangeId: Guid.NewGuid()))).Error is null,
                "暂停作者");

            var first = await Run();
            var leads = (await Ask(new("leads"))).Leads!;
            Check(first.Status == "Succeeded" && leads.Rows is [{ Account: "1556808774", Url: "https://xueqiu.com/1556808774/111" }]
                && leads.Coverage.Single(item => item.Account == paused.Account).State == "NeverChecked" &&
                leads.Coverage.Any(item => item is { State: "Read", Matches: 0 }),
                "解析 Atom 与 Google 包装链接，排除暂停作者、第三方网站，并记录未命中");
            Check((await Run()).Status == "Succeeded" && (await Ask(new("leads"))).Leads!.Total == 1,
                "重复订阅读回不重复保存");
            var limited = await Run();
            Check(limited is { Status: "RetryDeferred" } && limited.RetryAfter >= TimeSpan.FromMinutes(3) &&
                (await Ask(new("leads"))).Leads is { Total: 1, Coverage: var coverage } &&
                coverage.Any(item => item is { State: "RateLimited", Matches: null }),
                "限流保留既有线索并明确标记覆盖未知");
            Check((await Run()).Status == "Failed" &&
                (await Ask(new("leads"))).Leads!.Coverage.Any(item => item.State == "InvalidFeed"),
                "拒绝可携带外部实体的 XML");
            Check((await Configure("")).Error is null &&
                (await Ask(new("leads"))).Leads is { Configured: false, Total: 1, Coverage: var after } &&
                after.All(item => item.State == "NeverChecked"),
                "停用保留已有线索并清除过期的订阅覆盖");

            Task<WatchReply> Sync(LeadSubscription state, string url = FeedUrl) =>
                Ask(new("lead-feed-sync", ChangeId: Guid.NewGuid())
                {
                    FeedScope = state.Scope, PreviousFeedFingerprint = state.FeedFingerprint, FeedUrl = url
                });
            Check((await Configure(FeedUrl)).Error is null, "装载旧版手工配置的订阅");
            var legacy = (await Ask(new("lead-subscription"))).Subscription!;
            Check(legacy.Pending && legacy.AppliedScope is null && legacy.FeedFingerprint == LeadSubscription.Fingerprint(FeedUrl) &&
                !WatchProtocol.Encode(legacy).Contains(FeedUrl, StringComparison.Ordinal),
                "待接管的旧订阅只返回指纹");
            Check((await Sync(legacy)).Error is null && (await Ask(new("lead-subscription"))).Subscription is { Pending: false },
                "接管相同订阅后记录已同步作者范围");
            var synchronized = (await Ask(new("lead-subscription"))).Subscription!;
            var added = new Author("9100000000", "隔离测试作者");
            Check((await Ask(new("author-save", Author: added, ChangeId: Guid.NewGuid()))).Error is null,
                "新增作者会产生待同步范围");
            var pending = (await Ask(new("lead-subscription"))).Subscription!;
            Check(pending.Pending && pending.Scope.Contains(added.Account, StringComparison.Ordinal) &&
                (await Sync(synchronized)).Error is not null && (await Sync(pending, "https://www.google.com/alerts/feeds/other/feed")).Error is not null,
                "拒绝过时的作者范围和不同 Google 账号的订阅");
            Check((await Run()).Status == "Succeeded" && (await Ask(new("leads"))).Leads is { SubscriptionPending: true } pendingLeads &&
                pendingLeads.Coverage.Single(item => item.Account == added.Account).State == "NeverChecked",
                "同步前读取旧订阅时保留记录且不将新增作者标成已检查");
            Check((await Sync(pending)).Error is null && (await Ask(new("leads"))).Leads is { SubscriptionPending: false, Total: 1 },
                "相同 RSS 地址可更新查询范围且保留已有线索");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void SeedVersionOne(string root, byte[] key)
    {
        // 初始化独立测试库后移除新版增量表，得到带已有作者资料的 v1 库。
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
        ddl.CommandText = "DROP TABLE post_leads; DROP TABLE lead_coverage; DROP TABLE lead_feed; PRAGMA user_version=1";
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
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == FeedUrl, "只读取配置的订阅入口");
            if (++attempts == 3)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
                return Task.FromResult(limited);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(attempts == 4 ? """<!DOCTYPE rss SYSTEM "file:///unused"><rss/>""" : """
                    <feed xmlns="http://www.w3.org/2005/Atom">
                       <entry><link href="https://www.google.com/url?sa=t&amp;url=https%3A%2F%2Fwww.xueqiu.com%2F1556808774%2F111" /></entry>
                       <entry><link href="https://xueqiu.com/1556808774/111?tracking=1" /></entry>
                       <entry><link href="https://xueqiu.com/8790885129/999" /></entry>
                       <entry><link href="https://example.com/1556808774/112" /></entry>
                    </feed>
                    """)
            });
        }
    }
}
