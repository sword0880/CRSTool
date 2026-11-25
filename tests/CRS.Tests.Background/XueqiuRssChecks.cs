using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Plugins.XueqiuWatch.Jobs;
using CRS.Worker.Abstractions;
using Microsoft.Data.Sqlite;

internal static class XueqiuRssChecks
{
    private const string FeedUrl = "https://www.google.com/alerts/feeds/123456789/987654321";
    private const string ExternalFeedUrl = "https://feeds.example.invalid/author.xml";
    private const string FullTextFeedUrl = "https://feeds.example.invalid/authorized.json";

    internal static async Task RunAsync()
    {
        // 覆盖多语言账号入口、旧指纹兼容及多账号拒绝。
        var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test@example.com")));
        Check(GoogleAlertsAccountIdentity.ResolveFingerprint(["Google 帐号：测试 (TEST@example.com)", "Google Account: test@example.com"]) == fingerprint,
            "Google 账号入口兼容中英文并保持原指纹");
        Check(GoogleAlertsAccountIdentity.ResolveFingerprint(["Google 帳戶：test@example.com"]) == fingerprint,
            "Google 账号入口兼容繁体和无括号格式");
        Check(GoogleAlertsAccountIdentity.ResolveFingerprint(["Google Account: test@example.com", "Google Account: other@example.com"]) is null &&
            GoogleAlertsAccountIdentity.ResolveFingerprint(["RSS deliver to test@example.com"]) is null,
            "不从普通投递邮箱或多个账号推断当前身份");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var root = Path.Combine(Path.GetTempPath(), "CRS.XueqiuRss", Guid.NewGuid().ToString("N"));
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            SeedVersionOne(root, key);
            var transport = new FeedTransport();
            await using var session = new XueqiuBackgroundEntry().CreateSession(new Context(root, key), transport);
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

            async Task Restart()
            {
                await session.StopAsync(token);
                await session.StartAsync(token);
            }
            await VerifyGoogleAlertLifecycleAsync(Ask, Restart);
            await VerifyGoogleAlertAccountSwitchAsync(Path.Combine(root, "account-switch"), key, token);
            await VerifyRssProxyAsync(Ask, Restart, Run, token);
            await VerifyNeutralOpinionAsync(Ask);
            var bodyUrl = "https://xueqiu.com/9650668145/111";
            await XueqiuPostBodyChecks.RunAsync(Ask, "9650668145", bodyUrl, Restart);
            Check((await Ask(new("leads"))).Leads!.Rows.Single(x => x.Url == bodyUrl).PostBodyRevision == 2,
                "线索中的全文状态随手工补录刷新");
            await VerifyFullTextFeedAsync(Ask, Run, transport);
            await VerifyOriginalPagesAsync(Path.Combine(root, "original-pages"), key, token);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task VerifyOriginalPagesAsync(string root, byte[] key, CancellationToken token)
    {
        const string account = "9650668145";
        var reader = new SampleOriginalPages();
        await using var session = new XueqiuBackgroundEntry().CreateSession(new Context(root, key),
            new FeedTransport { FullTextEnabled = true }, (_, _) => Task.FromResult<IOriginalPageReader>(reader));
        await session.StartAsync(token);
        async Task<WatchReply> Ask(WatchRequest request) => WatchProtocol.Decode<WatchReply>(
            await ((IBackgroundPrivateDataSession)session).AccessPrivateDataAsync(WatchProtocol.Encode(request), token));
        var author = (await Ask(new("authors"))).Authors!.Single(item => item.Account == account);
        var save = await Ask(new("author-save", Author: author with { FeedUrl = FeedUrl }, ChangeId: Guid.NewGuid()));
        Check(save.Error is null, "保存静态原帖采集样本作者");
        var result = await session.ExecuteAsync(WatchProtocol.LeadJobId, token);
        var posts = (await Ask(new("leads"))).Leads!.Rows;
        var complete = posts.Single(item => item.Url.EndsWith("/111", StringComparison.Ordinal));
        var partial = posts.Single(item => item.Url.EndsWith("/441", StringComparison.Ordinal));
        var restricted = posts.Single(item => item.Url.EndsWith("/442", StringComparison.Ordinal));
        var bytes = (await Ask(new("post-body-read", Body: new(account, complete.Url, 1)))).BodyBytes;
        Check(result is { Status: "RetryDeferred" } && complete.BodySource == PostBodySource.OriginalPage &&
            complete.PostBodyRevision == 1 && complete.Summary.Contains("中国移动") &&
            bytes is not null && PostBody.Decode(bytes) == "第一段。\n第二段。",
            "Google Alerts 摘要与原帖全文分别保存，保留原帖换行和来源");
        Check(partial is { OriginalPageState: "BodyUnavailable", PostBodyRevision: 0 } &&
            restricted is { OriginalPageState: "RateLimited", PostBodyRevision: 0 } && reader.Calls == 3,
            "不完整的原帖和限流页面不会标为全文，第三方链接未打开");
        Check(reader.ShortestInterval >= TimeSpan.FromMilliseconds(900), "逐条打开原帖，核对随机等待的最短间隔");
        _ = await session.ExecuteAsync(WatchProtocol.LeadJobId, token);
        Check(reader.Calls == 3, "Google Alerts 304 时不重复读取已完成或尚在等待的原帖");
    }

    private sealed class SampleOriginalPages : IOriginalPageReader
    {
        private long? lastOpened;
        internal int Calls { get; private set; }
        internal TimeSpan ShortestInterval { get; private set; } = TimeSpan.MaxValue;

        public Task<OriginalPageRead> ReadAsync(OriginalPageLead post, CancellationToken token)
        {
            var now = Stopwatch.GetTimestamp();
            if (lastOpened is { } last)
                ShortestInterval = TimeSpan.FromTicks(Math.Min(ShortestInterval.Ticks, Stopwatch.GetElapsedTime(last, now).Ticks));
            lastOpened = now;
            Calls++;
            return Task.FromResult(post.Url.Split('/')[^1] switch
            {
                "111" => new OriginalPageRead("FullText", "第一段。\n第二段。", WatchProtocol.BeijingNow.AddHours(-1)),
                "441" => new OriginalPageRead("BodyUnavailable"),
                "442" => new OriginalPageRead("RateLimited", RetryAfter: TimeSpan.FromMinutes(3)),
                _ => throw new InvalidOperationException("意外的原帖浏览请求")
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task VerifyFullTextFeedAsync(Func<WatchRequest, Task<WatchReply>> ask,
        Func<Task<BackgroundJobResult>> run, FeedTransport transport)
    {
        const string authorId = "9650668145";
        const string sourcePrefix = "https://xueqiu.com/9650668145/";
        Check(WatchInput.IsGoogleAlertsFeed(new Uri("https://www.google.com.hk/alerts/feeds/42/43")),
            "支持 Google 快讯实际提供的香港区订阅地址");
        var author = (await ask(new("authors"))).Authors!.Single(x => x.Account == authorId);
        Task<WatchReply> Save(string source) => ask(new("author-save",
            Author: author with { FullTextFeedUrl = source }, ChangeId: Guid.NewGuid()));
        Check((await Save(FeedUrl)).Error is not null && (await Save(FullTextFeedUrl)).Error is null,
            "全文来源独立授权，Google Alerts 搜索片段不能升级为全文");

        transport.FullTextEnabled = true;
        Check((await run()).Status == "Succeeded", "发现任务同时完成全文补全");
        var page = (await ask(new("leads"))).Leads!;
        var source = new PostBodyRequest(authorId, sourcePrefix + "441");
        var metadata = (await ask(new("post-body-info", Body: source))).Body;
        var bytes = (await ask(new("post-body-read", Body: source with { Revision = 1 }))).BodyBytes!;
        Check(page.Rows.Single(x => x.Url == source.Url) is { PostBodyRevision: 1, BodySource: PostBodySource.AuthorizedFeed } &&
            metadata is { Revision: 1, Source: PostBodySource.AuthorizedFeed } &&
            PostBody.Decode(bytes) == "第一段。\n第二段：看好中国移动。" &&
            page.Coverage.Single(x => x.Account == authorId).FullTextCount == 1,
            "按原帖匹配获授权全文，正文保留换行并记录来源");
        Check(page.Rows.Single(x => x.Url == sourcePrefix + "442").PostBodyRevision == 0 &&
            (await ask(new("post-body-info", Body: source with { Url = sourcePrefix + "999" }))).Body is null &&
            page.Rows.Single(x => x.Url == sourcePrefix + "111") is { PostBodyRevision: 2, BodySource: PostBodySource.Manual },
            "摘要和未发现原帖不入全文，已确认的手工正文保留");
        Check((await run()).Status == "Succeeded" &&
            (await ask(new("leads"))).Leads!.Coverage.Single(x => x.Account == authorId).FullTextCount == 0,
            "重跑自动补全不会重复写入同一原帖");
    }

    private static async Task VerifyRssProxyAsync(Func<WatchRequest, Task<WatchReply>> ask, Func<Task> restart,
        Func<Task<BackgroundJobResult>> run, CancellationToken token)
    {
        Task<WatchReply> SaveProxy(string address) => ask(new("rss-proxy-save", ChangeId: Guid.NewGuid(), RssProxy: address));
        Check((await ask(new("rss-proxy"))).RssProxy == "" &&
            (await SaveProxy("http://proxy.example.invalid:7890")).Error is not null &&
            (await SaveProxy("socks5://127.0.0.1:7890")).Error is not null,
            "旧库代理默认关闭，只接收本机 HTTP / Mixed 地址");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"http://{listener.LocalEndpoint}/";
        Check((await SaveProxy(address)).Error is null, "保存 Mihomo 本机地址");
        await restart();
        Check((await ask(new("rss-proxy"))).RssProxy == address, "服务重启保留代理配置");

        var authors = (await ask(new("authors"))).Authors!;
        foreach (var (account, feed) in new[] { ("9650668145", FeedUrl), ("8790885129", ExternalFeedUrl) })
        {
            var author = authors.Single(x => x.Account == account);
            Check((await ask(new("author-save", Author: author with { FeedUrl = feed }, ChangeId: Guid.NewGuid()))).Error is null,
                "配置代理分流样本");
        }

        var response = RejectGoogleConnectAsync(listener, token);
        var result = await run();
        var leads = (await ask(new("leads"))).Leads!;
        Check(await response == "CONNECT www.google.com:443 HTTP/1.1" &&
            result.Status == "Succeeded" && leads.Total == 1 &&
            leads.Coverage.Single(x => x.Account == "9650668145").State == "ProxyUnavailable" &&
            leads.Coverage.Single(x => x.Account == "8790885129").State == "NotModified",
            "Google RSS 经本机代理，代理拒绝时保留历史，其他 Feed 仍走原有通道");

        Check((await SaveProxy("")).Error is null && (await ask(new("rss-proxy"))).RssProxy == "",
            "清空配置恢复默认网络");
    }

    private static async Task<string?> RejectGoogleConnectAsync(TcpListener listener, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var request = await reader.ReadLineAsync(token);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
        return request;
    }

    private static async Task VerifyNeutralOpinionAsync(Func<WatchRequest, Task<WatchReply>> ask)
    {
        const string account = "9650668145";
        var security = new Security("沪市", "601899", "紫金矿业", "有色金属");
        var opinion = new Opinion(Guid.NewGuid(), account, security, WatchProtocol.BeijingNow.AddHours(-2),
            Stance.Bullish, Attribution.Author, "看好", "", $"https://xueqiu.com/{account}/701");
        var neutral = opinion with { Id = Guid.NewGuid(), PublishedAt = opinion.PublishedAt.AddMinutes(30),
            Stance = Stance.Neutral, Content = "暂时观望", Url = $"https://xueqiu.com/{account}/702" };
        var pending = neutral with { Id = Guid.NewGuid(), PublishedAt = neutral.PublishedAt.AddMinutes(30),
            Stance = Stance.Pending, Content = "待核对", Url = $"https://xueqiu.com/{account}/703" };
        foreach (var item in new[] { opinion, neutral, pending })
            Check((await ask(new("opinion-save", Opinion: item, ChangeId: Guid.NewGuid()))).Error is null,
                "保存观点统计样本");
        var overview = (await ask(new("overview"))).Overview!;
        var detail = (await ask(new("detail", new(Security: security)))).Detail!;
        Check(overview.Stocks is [{ Bullish: 0, Bearish: 0 }] &&
            detail.Rows.Length == 1 && detail.Rows[0].Id == neutral.Id,
            "最新中性观点撤销旧看多计数，待确认发言不覆盖中性观点");
    }

    private static async Task VerifyGoogleAlertLifecycleAsync(Func<WatchRequest, Task<WatchReply>> ask, Func<Task> restart)
    {
        const string account = "1111222233";
        const string googleAccount = "sample-google-account";
        const string remote = "sample-alert";

        async Task<Author> AuthorAsync() => (await ask(new("authors"))).Authors!.Single(x => x.Account == account);
        async Task<GoogleAlertSubscription> SubscriptionAsync() =>
            (await ask(new("alert-subscription", new(Account: account)))).Alert!;
        Task<WatchReply> SaveAsync(Author author) => ask(new("author-save", Author: author, ChangeId: Guid.NewGuid()));
        Task<WatchReply> UpdateAsync(string operation, GoogleAlertUpdate result) =>
            ask(new(operation, ChangeId: Guid.NewGuid(), Alert: result));

        Check((await SaveAsync(new(account, "同步测试作者"))).Error is null, "添加作者写入订阅同步意图");
        var pending = await SubscriptionAsync();
        await restart();
        Check(pending.State == GoogleAlertState.PendingCreate && pending.OperationId != Guid.Empty &&
            await SubscriptionAsync() == pending, "重启保留待创建操作及标识");

        var create = new GoogleAlertUpdate(account, pending.OperationId, googleAccount);
        Check((await UpdateAsync("alert-sync-begin", create)).Error is null &&
            (await UpdateAsync("alert-sync-begin", create)).Error is null, "相同账号重试同一次创建操作");
        Check((await UpdateAsync("alert-sync-complete", create with
        {
            Outcome = GoogleAlertOutcome.Created, RemoteId = remote, FeedUrl = "https://other.example/alerts/feeds/1/2"
        })).Error is not null, "拒绝将非 Google 地址作为自动订阅结果");

        var archived = (await AuthorAsync()) with { Archived = true, Active = false };
        Check((await SaveAsync(archived)).Error is null, "创建过程中允许移除作者");
        var created = create with { Outcome = GoogleAlertOutcome.Created, RemoteId = remote, FeedUrl = FeedUrl };
        Check((await UpdateAsync("alert-sync-complete", created)).Error is null &&
            await SubscriptionAsync() is { State: GoogleAlertState.PendingDelete, RemoteId: remote } &&
            await AuthorAsync() is { Archived: true, Active: false }, "迟到的创建结果进入待删除且保持停止采集");

        var restored = (await AuthorAsync()) with { Archived = false, Active = true };
        Check((await SaveAsync(restored)).Error is null && await AuthorAsync() is { AlertState: GoogleAlertState.Managed, FeedUrl: FeedUrl },
            "删除开始前恢复作者复用托管 RSS");
        Check((await SaveAsync((await AuthorAsync()) with { Active = false })).Error is null &&
            await AuthorAsync() is { AlertState: GoogleAlertState.Managed, FeedUrl: FeedUrl, Active: false },
            "暂停作者保留托管提醒与 RSS");
        Check((await SaveAsync((await AuthorAsync()) with { Archived = true })).Error is null,
            "移除已托管作者再次排队删除");

        var deletion = await SubscriptionAsync();
        var delete = new GoogleAlertUpdate(account, deletion.OperationId, googleAccount, RemoteId: remote);
        Check((await UpdateAsync("alert-sync-begin", delete with { GoogleAccount = "another-account" })).Error is not null &&
            (await UpdateAsync("alert-sync-begin", delete)).Error is null, "仅由绑定的 Google 账号启动删除核对");
        await restart();
        Check(await SubscriptionAsync() is { State: GoogleAlertState.Deleting } &&
            (await UpdateAsync("alert-sync-complete", delete with { Outcome = GoogleAlertOutcome.Missing, RemoteId = "other" })).Error is not null,
            "重启后保留待核对删除并拒绝不匹配的远端标识");

        Check((await SaveAsync((await AuthorAsync()) with { Archived = false, Active = true })).Error is null &&
            (await AuthorAsync()).FeedUrl.Length == 0 &&
            (await UpdateAsync("alert-sync-complete", delete with { Outcome = GoogleAlertOutcome.Missing })).Error is null &&
            await SubscriptionAsync() is { State: GoogleAlertState.PendingCreate },
            "删除期间重新添加会等待删除确认再创建");

        var external = (await AuthorAsync()) with { FeedUrl = FeedUrl };
        Check((await SaveAsync(external)).Error is null && await SubscriptionAsync() is { State: GoogleAlertState.External } &&
            (await SaveAsync((await AuthorAsync()) with { Archived = true })).Error is null &&
            await SubscriptionAsync() is { State: GoogleAlertState.None } && (await AuthorAsync()).FeedUrl.Length == 0,
            "移除外部 RSS 只解除本地绑定，不排队删除远端提醒");
    }

    private static async Task VerifyGoogleAlertAccountSwitchAsync(string root, byte[] key, CancellationToken token)
    {
        const string authorId = "4444555566";
        const string newFeed = "https://www.google.com/alerts/feeds/new-account/new-alert";
        var oldAccount = new string('A', 64);
        var newAccount = new string('B', 64);
        await using var session = new XueqiuBackgroundEntry().CreateSession(new Context(root, key), new FeedTransport());
        await session.StartAsync(token);
        async Task<WatchReply> Ask(WatchRequest request) => WatchProtocol.Decode<WatchReply>(
            await ((IBackgroundPrivateDataSession)session).AccessPrivateDataAsync(WatchProtocol.Encode(request), token));
        Task<WatchReply> Save(WatchRequest request) => Ask(request with { ChangeId = Guid.NewGuid() });
        async Task<Author> Current() => (await Ask(new("authors"))).Authors!.Single(author => author.Account == authorId);
        async Task<GoogleAlertSubscription> Subscription() =>
            (await Ask(new("alert-subscription", new(Account: authorId)))).Alert!;
        Task<WatchReply> Switch(string from, string to) => Save(new("alert-account-switch", ConnectedGoogleAccount: to,
            ExpectedGoogleAccount: from));
        Task<WatchReply> Update(string operation, GoogleAlertUpdate update) => Save(new(operation, Alert: update));

        Check((await Switch("", oldAccount)).Error is null &&
            (await Save(new("author-save", Author: new(authorId, "换号测试")))).Error is null, "连接旧账号并新增测试作者");
        var old = new GoogleAlertUpdate(authorId, (await Subscription()).OperationId, oldAccount);
        Check((await Update("alert-sync-begin", old)).Error is null &&
            (await Update("alert-sync-complete", old with { Outcome = GoogleAlertOutcome.Created,
                RemoteId = "old-alert", FeedUrl = FeedUrl })).Error is null &&
            await Current() is { AlertState: GoogleAlertState.Managed, GoogleAlertVerifiedAt: not null },
            "已核实创建的提醒记录网页核实时间");
        Check((await Switch("", newAccount)).Error is not null && (await Switch(oldAccount, newAccount)).Error is null &&
            await Current() is { AlertState: GoogleAlertState.External, FeedUrl: FeedUrl, GoogleAlertVerifiedAt: null } &&
            (await Update("alert-sync-begin", old)).Error is not null,
            "切换账号保护旧 RSS 采集并撤销旧账号的网页操作资格及核实状态");

        var verify = new GoogleAlertUpdate(authorId, Guid.Empty, newAccount, GoogleAlertOutcome.Missing,
            AuthorRevision: (await Current()).Revision);
        Check((await Update("alert-sync-verify", verify with { GoogleAccount = oldAccount })).Error is not null &&
            (await Update("alert-sync-verify", verify)).Error is null &&
            await Current() is { AlertState: GoogleAlertState.PendingCreate, FeedUrl: FeedUrl, GoogleAlertVerifiedAt: null },
            "新账号找不到提醒时补建，同时继续使用原 RSS");
        var create = new GoogleAlertUpdate(authorId, (await Subscription()).OperationId, newAccount);
        Check((await Update("alert-sync-begin", create)).Error is null &&
            (await Update("alert-sync-complete", create with { Outcome = GoogleAlertOutcome.Missing })).Error is null &&
            (await Current()).AlertState == GoogleAlertState.PendingCreate,
            "重试补建时核对到缺失，继续排队并保留备用 RSS");
        create = create with { OperationId = (await Subscription()).OperationId };
        Check((await Update("alert-sync-begin", create)).Error is null &&
            (await Update("alert-sync-complete", create with { Outcome = GoogleAlertOutcome.Created,
                RemoteId = "new-alert", FeedUrl = newFeed })).Error is null &&
            await Current() is { AlertState: GoogleAlertState.Managed, FeedUrl: newFeed, GoogleAlertVerifiedAt: not null },
            "新提醒核实后切换采集 RSS");

        verify = verify with { AuthorRevision = (await Current()).Revision };
        Check((await Update("alert-sync-verify", verify with { Outcome = GoogleAlertOutcome.NonRss })).Error is null &&
            await Current() is { FeedUrl: newFeed, GoogleAlertVerifiedAt: null } &&
            (await Update("alert-sync-verify", verify with { Outcome = GoogleAlertOutcome.Existing,
                RemoteId = "new-alert", FeedUrl = newFeed })).Error is null,
            "网页缺少 RSS 时撤销核实状态，恢复后重新核实同一提醒");
        await session.StopAsync(token);
        await session.StartAsync(token);
        Check(await Current() is { AlertState: GoogleAlertState.Managed, FeedUrl: newFeed, GoogleAlertVerifiedAt: not null },
            "重启保留同一账号的网页核实结果及托管身份");

        Check((await Switch(newAccount, oldAccount)).Error is null &&
            (await Update("alert-sync-verify", verify with { GoogleAccount = oldAccount, Outcome = GoogleAlertOutcome.Existing,
                RemoteId = "old-alert", FeedUrl = FeedUrl })).Error is null &&
            await Current() is { AlertState: GoogleAlertState.External, FeedUrl: FeedUrl, GoogleAlertVerifiedAt: not null },
            "切回旧账号时复用仍在网页上的 RSS，不重复创建");
        Check((await Save(new("author-save", Author: (await Current()) with { Active = false }))).Error is null &&
            (await Update("alert-sync-verify", verify with { GoogleAccount = oldAccount,
                AuthorRevision = (await Current()).Revision })).Error is null &&
            await Current() is { Active: false, AlertState: GoogleAlertState.External, GoogleAlertVerifiedAt: null },
            "已暂停作者可以核对网页状态，缺失时不补建");
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
        using var clearSubscriptionSchema = db.CreateCommand();
        clearSubscriptionSchema.CommandText =
            "DROP TABLE google_alerts; DROP TABLE rss_proxy; DROP TABLE post_bodies; DROP TABLE full_text_coverage; DROP TABLE google_alert_connection; DROP TABLE original_page_checks";
        clearSubscriptionSchema.ExecuteNonQuery();
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
        public bool FullTextEnabled { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.AbsoluteUri == ExternalFeedUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            if (request.RequestUri?.AbsoluteUri == FullTextFeedUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {
                      "version":"https://jsonfeed.org/version/1.1",
                      "title":"授权全文样本",
                      "items":[
                        {"id":"441","url":"https://xueqiu.com/9650668145/441","content_text":"第一段。\n第二段：看好中国移动。","date_published":"2026-11-23T01:01:00Z"},
                        {"id":"442","url":"https://xueqiu.com/9650668145/442","summary":"只有摘要","date_published":"2026-11-23T01:01:00Z"},
                        {"id":"999","url":"https://xueqiu.com/9650668145/999","content_text":"未发现的原帖","date_published":"2026-11-23T01:01:00Z"},
                        {"id":"111","url":"https://xueqiu.com/9650668145/111","content_text":"不会覆盖手工正文","date_published":"2026-11-23T01:01:00Z"},
                        {"id":"443","url":"https://xueqiu.com/8790885129/443","content_text":"另一位作者","date_published":"2026-11-23T01:01:00Z"}
                      ]
                    }
                    """) });
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
            if (FullTextEnabled) body = body.Replace("</feed>", """
                <entry><id>441</id><link href="https://xueqiu.com/9650668145/441"/><summary>搜索片段</summary></entry>
                <entry><id>442</id><link href="https://xueqiu.com/9650668145/442"/><summary>第二条搜索片段</summary></entry>
                </feed>
                """, StringComparison.Ordinal);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }
}
