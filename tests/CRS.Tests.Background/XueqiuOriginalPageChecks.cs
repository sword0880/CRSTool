using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Plugins.XueqiuWatch.Jobs;
using CRS.Worker.Ipc;

internal static class XueqiuOriginalPageChecks
{
    private static OriginalPageLead PublicPost =>
        new("9976484464", "https://xueqiu.com/9976484464/413882047", 0, 0);

    internal static async Task RunFixtureAsync()
    {
        var post = new OriginalPageLead("123456", "https://xueqiu.com/123456/987654", 0, 0);
        const string payload = """
            {"id":987654,"user_id":"123456","user":{"id":123456},"created_at":1704157320000,"truncated":false,
            "text":"<p>第一段。<script>脚本内容</script></p><p><a title='a > b'>第二段</a> &amp; 同段。<br>第三段。</p>",
            "retweeted_status":{"text":"其他作者原帖"}}
            """;
        var transport = new PostTransport { Detail = () => Json(payload) };
        await using var api = new XueqiuPostApi(transport);
        Task<OriginalPageRead> Read() => api.ReadAsync(post, CancellationToken.None);
        var result = await Read();
        Check(result is { State: "FullText", Text: "第一段。\n第二段 & 同段。\n第三段。" } &&
            result.Published == DateTimeOffset.FromUnixTimeMilliseconds(1704157320000),
            "API 核对发布时间、保留段落和实体，排除脚本和被转发帖正文");

        transport.Detail = () => Json(payload.Replace("\"truncated\":false", "\"truncated\":true", StringComparison.Ordinal));
        Check(await Read() is { State: "BodyUnavailable", Published: not null, Text: null },
            "API 标记截断时保留已核实的时间");
        transport.Detail = () => Json(payload.Replace("第三段。", "展开全文", StringComparison.Ordinal));
        Check(await Read() is { State: "BodyUnavailable", Published: not null, Text: null },
            "出现展开全文提示时只保留已核实的时间");
        transport.Detail = () => Json(payload.Replace("\"user\":{\"id\":123456}", "\"user\":{\"id\":654321}", StringComparison.Ordinal));
        Check(await Read() is { State: "BodyUnavailable", Published: null }, "拒绝作者 ID 冲突的详情");
        transport.Detail = () => Json(payload.Replace("\"id\":987654", "\"id\":987655", StringComparison.Ordinal));
        Check(await Read() is { State: "BodyUnavailable", Published: null }, "拒绝不同帖子 ID 的详情");
        transport.Detail = () => Json(payload.Replace("1704157320000", "0", StringComparison.Ordinal));
        Check(await Read() is { State: "BodyUnavailable", Published: null }, "拒绝占位发布时间");
        transport.Timeline = () => Json("""
            {"maxPage":5,"statuses":[
              {"id":987654,"user_id":"123456","user":{"id":123456},"created_at":1704157320000},
              {"id":987655,"user_id":"123456","user":{"id":654321},"created_at":1704157320000},
              {"id":987656,"user_id":"123456","created_at":0}]}
            """);
        var page = await api.ReadTimelineAsync(post.Account, 2, CancellationToken.None);
        Check(page is { State: "Read", Returned: 3, LastPage: 5, Posts: [{ Id: "987654" }] },
            "动态接口复用会话并校验每条作者、帖子 ID 和时间");
        Check(transport.SessionCalls == 1, "同批详情复用一次匿名会话");

        transport.Detail = () => Json("{\"error_code\":\"10020\"}");
        Check(await Read() is { State: "AccessRestricted" } && transport.SessionCalls == 2,
            "匿名会话失效只刷新一次");
        transport.Detail = () => new((HttpStatusCode)429)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3)) }
        };
        Check(await Read() is { State: "RateLimited", HttpStatus: 429, RetryAfter.TotalMinutes: 3 },
            "HTTP 429 采用 Retry-After");
        transport.Detail = () => new(HttpStatusCode.OK) { Content = new StringContent("<html>访问验证</html>", Encoding.UTF8, "text/html") };
        Check(await Read() is { State: "AccessRestricted" }, "HTTP 200 的非 JSON 验证页标为访问受限");
    }

    internal static async Task RunLiveAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var api = XueqiuPostApi.Open(null);
        var result = await api.ReadAsync(PublicPost, deadline.Token);
        if (result.State != "FullText" || result is not { Text.Length: > 1000, Published: not null } ||
            result.Text.Contains("全部讨论", StringComparison.Ordinal))
            throw new InvalidOperationException($"公开原帖 API 读取结果：{result.State}（HTTP {result.HttpStatus}）");
        Console.WriteLine($"PASS 雪球公开长文通过匿名 API 读取并核对身份，正文 {result.Text.Length} 字符。");
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private sealed class PostTransport : HttpMessageHandler
    {
        internal required Func<HttpResponseMessage> Detail { private get; set; }
        internal Func<HttpResponseMessage>? Timeline { private get; set; }
        internal int SessionCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://www.xueqiu.com/hq")
            {
                SessionCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            if (url == "https://api.xueqiu.com/v4/statuses/user_timeline.json?user_id=123456&count=20&page=2" &&
                request.Method == HttpMethod.Get && Timeline is { } readTimeline)
                return Task.FromResult(readTimeline());
            if (url != "https://api.xueqiu.com/statuses/show.json?id=987654" || request.Method != HttpMethod.Get)
                throw new InvalidOperationException("意外的原帖请求地址。");
            return Task.FromResult(Detail());
        }
    }

    internal static async Task RunWorkerAsync(BackgroundPipeClient client, CancellationToken token)
    {
        var account = PublicPost.Account;

        async Task<WatchReply> Ask(WatchRequest request)
        {
            var command = new BackgroundRequest(WorkerProtocol.Version, Guid.NewGuid(), WorkerProtocol.PrivatePluginData,
                WatchProtocol.PluginId, Payload: WatchProtocol.Encode(request));
            while (true)
            {
                var response = await client.SendAsync(command, token);
                if (response.Code is "Busy" or "PluginBusy" or "RateLimited")
                {
                    await Task.Delay(250, token);
                    continue;
                }
                if (response.Code != "Ok" || response.Payload is null)
                    throw new InvalidOperationException($"隔离作者操作失败：{response.Code}");
                var result = WatchProtocol.Decode<WatchReply>(response.Payload);
                if (result.Error is not null) throw new InvalidOperationException("隔离作者操作失败。");
                return result;
            }
        }

        var authors = (await Ask(new("authors"))).Authors!;
        foreach (var author in authors)
            await Ask(new("author-save", Author: author with { Active = false }, ChangeId: Guid.NewGuid()));
        var earliest = WatchProtocol.BeijingNow.AddDays(-WatchProtocol.NewAuthorHistoryDays).AddSeconds(-1);
        await Ask(new("author-save", Author: new(account, "公开回补样本"), ChangeId: Guid.NewGuid()));
        var queued = (await Ask(new("leads", new(Account: account)))).Leads!;
        Check(queued is { BackfillSupported: true } &&
            queued.Coverage.Single(item => item.Account == account).BackfillPending,
            "保存新作者后登记 30 天回补");

        var add = await client.SendAsync(new BackgroundRequest(WorkerProtocol.Version, Guid.NewGuid(), WorkerProtocol.AddPluginPlan,
            WatchProtocol.PluginId, WatchProtocol.LeadJobId, Cron: "0 15 9 * * ?", Payload: "Paused") { VpnNode = "" }, token);
        if (add.Code is not ("Ok" or "AcceptedNeedsReconcile"))
            throw new InvalidOperationException("原帖任务计划失败：" + add.Code);
        var run = new BackgroundRequest(WorkerProtocol.Version, Guid.NewGuid(), "RunNow", WatchProtocol.PluginId, WatchProtocol.LeadJobId);
        if ((await client.SendAsync(run, token)).Code != "Accepted")
            throw new InvalidOperationException("未能手动触发原帖任务。");
        while (true)
        {
            var history = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetHistory",
                WatchProtocol.PluginId, WatchProtocol.LeadJobId), token);
            if (history.History?.FirstOrDefault(item => item.EndedUtc is not null) is { } record)
            {
                var leads = (await Ask(new("leads", new(Account: account)))).Leads!;
                Check(leads is { Total: > 0, BackfillSupported: true } &&
                    leads.Coverage.Single(item => item.Account == account) is
                        { Timeline.State: "Read", BackfillPending: false } &&
                    leads.Rows.All(post => post.Account == account && post.OriginalPageTimeVerified && post.PublishedAt >= earliest),
                    $"隔离 Worker 仅凭账号完成 30 天回补，发现 {leads.Total} 条公开动态");
                Check(leads.Rows.Any(post => post.PostBodyRevision > 0 && post.BodySource == PostBodySource.OriginalApi),
                    "隔离 Worker 通过详情 API 保存公开原帖全文");
                return;
            }
            await Task.Delay(300, token);
        }
    }
}
