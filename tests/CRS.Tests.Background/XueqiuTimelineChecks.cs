using System.Net;
using System.Text;
using System.Text.Json;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Plugins.XueqiuWatch.Jobs;
using CRS.Worker.Abstractions;

internal static class XueqiuTimelineChecks
{
    private const string Primary = "1556808774";
    private const string ApiOnly = "1760673340";
    private const string BackupUrl = "https://www.google.com/alerts/feeds/123456/234567";

    internal static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var directory = Path.Combine(Path.GetTempPath(), "CRS.XueqiuTimeline", Guid.NewGuid().ToString("N"));
        var context = new FixtureContext(directory);
        var feed = new BackupFeed();
        var api = new TimelineFixture();
        await using var session = new XueqiuBackgroundEntry().CreateSession(context, feed, _ => api);
        await session.StartAsync(token);
        async Task<WatchReply> Ask(WatchRequest request) => WatchProtocol.Decode<WatchReply>(
            await ((IBackgroundPrivateDataSession)session).AccessPrivateDataAsync(WatchProtocol.Encode(request), token));
        async Task Save(Author author) =>
            Check((await Ask(new("author-save", Author: author, ChangeId: Guid.NewGuid()))).Error is null, "作者配置已保存");
        Task<BackgroundJobResult> Run() => session.ExecuteAsync(WatchProtocol.LeadJobId, token);
        async Task<LeadPage> Leads(string account) => (await Ask(new("leads", new(Account: account)))).Leads!;
        DateTimeOffset? Completed()
        {
            using var store = new WatchStore(directory, context.OpenDataKey());
            return store.TimelineCompleted(Primary);
        }

        foreach (var author in (await Ask(new("authors"))).Authors!)
            await Save(author with { Active = author.Account is Primary or ApiOnly,
                FeedUrl = author.Account == Primary ? BackupUrl : author.FeedUrl });

        Check((await Run()).Status == "Succeeded" && feed.Calls == 0, "主源成功时跳过已配置的备用 RSS");
        var first = await Leads(Primary);
        Check(first.Total == 2 && first.Rows.All(post => post.OriginalPageTimeVerified) && first.TimelineSupported &&
            first.Coverage.Single(c => c.Account == Primary).Timeline is { State: "Read", Pages: 2, Matches: 2 },
            "短页后继续翻页，置顶重复项去重，记录 API 的已核实时点");
        Check((await Leads(ApiOnly)).Total == 1, "无 RSS 的启用作者可直接发现新帖");
        var completed = Completed();
        Check(completed is not null, "已完成轮次持久化增量基准");

        api.Round = 2;
        Check((await Run()).Status == "Succeeded" && feed.Calls == 1, "翻页失败时回退到对应作者的 RSS");
        var fallback = await Leads(Primary);
        var shared = fallback.Rows.Single(post => post.Url.EndsWith("/123", StringComparison.Ordinal));
        Check(fallback.Total == 4 && shared.OriginalPageTimeVerified && shared.FeedItemId == "backup-123" &&
            shared.Title == "雪球标题" && shared.PublishedAt == api.NewPostTime.ToOffset(TimeSpan.FromHours(8)),
            "API 与 RSS 同一原帖合并，保留已核实时间，RSS 独有帖子仍可发现");
        Check(fallback.Coverage.Single(c => c.Account == Primary).Timeline is { State: "Unavailable", Pages: 1 } &&
            Completed() == completed, "翻页失败保存先前页，增量基准在失败时保持");

        api.Round = 3;
        Check((await Run()) is { Status: "RetryDeferred", RetryAfter: not null } && feed.Calls == 2,
            "主源限流时先保存备用 RSS 结果，再进入退避");
        var calls = api.Calls;
        await session.StopAsync(token);
        await session.StartAsync(token);
        Check((await Run()).Status == "RetryDeferred" && api.Calls == calls && feed.Calls == 3 &&
            (await Leads(Primary)).Total == 4, "重启后共享 API 退避仍生效，备用 RSS 可继续去重采集");

        using (var store = new WatchStore(directory, context.OpenDataKey()))
        {
            var text = new string('长', 2500);
            var posts = Enumerable.Range(200, WatchProtocol.LeadsPerPage).Select(id =>
                new TimelinePost(id.ToString(), "长帖", text, text, api.NewPostTime)).ToArray();
            store.SaveTimeline(ApiOnly, new("Read", WatchProtocol.BeijingNow, posts, 2), token);
        }
        var dense = await Leads(ApiOnly);
        var framed = JsonEncodedText.Encode(WatchProtocol.Encode(new WatchReply { Leads = dense })).EncodedUtf8Bytes.Length;
        Check(dense.Rows.Length == WatchProtocol.LeadsPerPage && framed <= 120 * 1024,
            "长帖密集时预览适配双层管道，仍返回完整的一页线索");
    }

    internal static async Task RunBackfillAsync()
    {
        const string account = "3333333333";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = deadline.Token;
        var directory = Path.Combine(Path.GetTempPath(), "CRS.XueqiuBackfill", Guid.NewGuid().ToString("N"));
        var context = new FixtureContext(directory);
        var api = new BackfillFixture(account);
        var feed = new BackupFeed();
        await using var session = new XueqiuBackgroundEntry().CreateSession(context, feed, _ => api);
        await session.StartAsync(token);
        async Task<WatchReply> Ask(WatchRequest request) => WatchProtocol.Decode<WatchReply>(
            await ((IBackgroundPrivateDataSession)session).AccessPrivateDataAsync(WatchProtocol.Encode(request), token));
        async Task<LeadPage> Leads() => (await Ask(new("leads", new(Account: account)))).Leads!;
        async Task Save(Author author)
        {
            if ((await Ask(new("author-save", Author: author, ChangeId: Guid.NewGuid()))).Error is not null)
                throw new InvalidOperationException("保存回补测试作者失败。");
        }
        foreach (var author in (await Ask(new("authors"))).Authors!)
            await Save(author with { Active = false });
        await Save(new(account, "新增作者"));
        await Save(new("4444444444", "暂停作者", Active: false));
        using (var store = new WatchStore(directory, context.OpenDataKey()))
            api.WindowEnd = store.PendingBackfill(account)!.WindowEnd!.Value;
        Check((await Leads()).Coverage.Single(row => row.Account == account).BackfillPending,
            "保存新作者即登记近 30 天回补");

        var first = await session.ExecuteAsync(WatchProtocol.LeadJobId, token);
        var partial = await Leads();
        Check(first.Status == "RetryDeferred" && api.Pages.SequenceEqual(Enumerable.Range(1, 8)) &&
            partial.Total == 8 && partial.Rows.All(row => row.PostBodyRevision == 1) &&
            partial.Coverage.Single(row => row.Account == account).Timeline?.State == "Backfilling",
            "从第一页跨多页收集公开动态，跳过旧置顶，分批核对正文");
        await session.StopAsync(token);
        await session.StartAsync(token);
        api.Pages.Clear();
        api.Shifted = true;
        var second = await session.ExecuteAsync(WatchProtocol.LeadJobId, token);
        var completed = await Leads();
        Check(second.Status == "Succeeded" && api.Pages.SequenceEqual(new[] { 7, 8, 9, 10, 11 }) &&
            completed.Total == 9 && completed.Rows.All(row => row.PostBodyRevision == 1) && feed.Calls == 0 &&
            completed.Rows.Any(row => row.PublishedAt == api.WindowEnd.AddDays(-WatchProtocol.NewAuthorHistoryDays)) &&
            !completed.Coverage.Single(row => row.Account == account).BackfillPending,
            "重启后通过锚点续页，正确处理页码变化，纳入第 30 天边界并排除更早的帖子");
    }

    private sealed class BackfillFixture(string account) : IAuthorTimelineReader, IOriginalPageReader
    {
        internal DateTimeOffset WindowEnd { get; set; }
        internal bool Shifted { get; set; }
        internal List<int> Pages { get; } = [];

        public Task<TimelinePage> ReadTimelineAsync(string requested, int page, CancellationToken token)
        {
            if (requested != account) throw new InvalidOperationException("暂停作者进入回补。");
            Pages.Add(page);
            var position = Shifted ? page - 1 : page;
            var post = new TimelinePost((100 + position).ToString(), "回补动态", "正文片段", "", Published(position));
            TimelinePost[] posts = page == 1 ? [new("99", "旧置顶", "", "", WindowEnd.AddDays(-60)), post] : [post];
            return Task.FromResult(new TimelinePage("Read", posts, posts.Length, 100));
        }

        public Task<OriginalPageRead> ReadAsync(OriginalPageLead post, CancellationToken token) =>
            Task.FromResult(new OriginalPageRead("FullText", "原帖正文。",
                Published(int.Parse(post.Url[(post.Url.LastIndexOf('/') + 1)..]) - 100)));

        private DateTimeOffset Published(int position) => position < 9 ? WindowEnd.AddDays(-2 * position) :
            WindowEnd.AddDays(-WatchProtocol.NewAuthorHistoryDays - (position - 9));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Check(bool result, string label)
    {
        if (!result) throw new InvalidOperationException(label);
        Console.WriteLine($"PASS 雪球主动发现：{label}");
    }

    private sealed class FixtureContext(string directory) : IBackgroundHostContext
    {
        public string PluginId => WatchProtocol.PluginId;
        public string DataDirectory { get; } = directory;
        public byte[] OpenDataKey() => new byte[32];
    }

    private sealed class TimelineFixture : IAuthorTimelineReader, IOriginalPageReader
    {
        private readonly DateTimeOffset seen = DateTimeOffset.FromUnixTimeSeconds(WatchProtocol.BeijingNow.ToUnixTimeSeconds());
        internal DateTimeOffset NewPostTime => seen.AddMinutes(-1);
        internal int Round { get; set; } = 1;
        internal int Calls { get; private set; }

        public Task<TimelinePage> ReadTimelineAsync(string account, int page, CancellationToken token)
        {
            Calls++;
            var pinned = new TimelinePost("121", "置顶", "", "", seen.AddDays(-2));
            var first = new TimelinePost("122", "动态", "", "", NewPostTime);
            var newest = new TimelinePost("123", "雪球标题", "接口片段", "", NewPostTime);
            TimelinePage result = (Round, account, page) switch
            {
                (1, Primary, 1) => new("Read", [pinned], 1, 10),
                (1, Primary, 2) => new("Read", [pinned, first], 2, 10),
                (1 or 2, ApiOnly, 1) => new("Read", [first], 1, 1),
                (2, Primary, 1) => new("Read", [pinned, newest], 2, 10),
                (2, Primary, 2) => new("Unavailable", []),
                (3, Primary, 1) => new("RateLimited", [], RetryAfter: TimeSpan.FromMinutes(3), HttpStatus: 429),
                _ => throw new InvalidOperationException($"意外地读取作者 {account} 的第 {page} 页。")
            };
            return Task.FromResult(result);
        }

        public Task<OriginalPageRead> ReadAsync(OriginalPageLead post, CancellationToken token) =>
            Task.FromResult(new OriginalPageRead("AccessRestricted"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BackupFeed : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri != BackupUrl) throw new InvalidOperationException("意外的 RSS 地址。");
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"""
                    <rss version="2.0"><channel><title>备用</title>
                    <item><guid>backup-123</guid><title>来源标题</title><link>https://xueqiu.com/{Primary}/123</link>
                    <pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate></item>
                    <item><guid>backup-124</guid><title>补充线索</title><link>https://xueqiu.com/{Primary}/124</link></item>
                    </channel></rss>
                    """, Encoding.UTF8, "application/rss+xml")
            });
        }
    }
}
