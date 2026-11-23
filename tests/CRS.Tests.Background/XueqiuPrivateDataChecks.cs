using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Ipc;

internal static class XueqiuPrivateDataChecks
{
    public static async Task RunAsync(BackgroundPipeClient client, string data, CancellationToken token)
    {
        async Task<WatchReply> Send(WatchRequest request)
        {
            var payload = WatchProtocol.Encode(request);
            BackgroundResponse response;
            while (true)
            {
                response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
                    WorkerProtocol.PrivatePluginData, WatchProtocol.PluginId, Payload: payload), token);
                if (response.Code is not ("RateLimited" or "Busy" or "PluginBusy")) break;
                await Task.Delay(250, token);
            }
            Require(response.Code == "Ok" && response.Payload is not null, $"private endpoint {response.Code}");
            return WatchProtocol.Decode<WatchReply>(response.Payload!);
        }

        var publicReply = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "QueryPlugin",
            WatchProtocol.PluginId, Payload: WatchProtocol.Encode(new WatchRequest("authors"))), token);
        Require(publicReply.Code != "Ok" && publicReply.Payload is null, "private records cannot be read through public query");

        var options = await client.QueryAsync(WorkerProtocol.GetPluginPlanOptions, token);
        Require(options.Jobs?.Any(plan => plan.PluginId == WatchProtocol.PluginId &&
            plan.JobId == WatchProtocol.LeadJobId && plan.Revision == 0 && !plan.Enabled) == true, "RSS plan available and unscheduled");
        Require((await Send(new("leads"))).Leads is { Configured: false, Total: 0 } initialLeads &&
            initialLeads.Authors.Length == WatchProtocol.InitialAuthors.Count,
            "RSS panel loads over the private pipe without exposing a subscription");

        var authors = (await Send(new("authors"))).Authors!;
        Require(authors.Length == WatchProtocol.InitialAuthors.Count && authors.Any(x => x.Name == "达人知命"),
            "seeded authors");
        var first = authors[0];
        var second = authors[1];
        Require((await Send(new("alert-subscription", new(Account: first.Account)))).Alert is
            { State: GoogleAlertState.PendingCreate }, "initial alert intent is readable over the private pipe");
        const string loopbackProxy = "http://127.0.0.1:7890/";
        var proxySave = new WatchRequest("rss-proxy-save", ChangeId: Guid.NewGuid(), RssProxy: loopbackProxy);
        Require((await Send(new("rss-proxy"))).RssProxy == "" &&
            (await Send(proxySave)).Error is null && (await Send(proxySave)).Error is null &&
            (await Send(new("rss-proxy"))).RssProxy == loopbackProxy,
            "RSS proxy settings round trip over the private pipe");
        var archive = new WatchRequest("author-save", Author: authors[7] with { Archived = true }, ChangeId: Guid.NewGuid());
        Require((await Send(archive)).Error is null && (await Send(archive)).Error is null, "author removal and exact replay");
        Require((await Send(new("authors"))).Authors!.Single(x => x.Account == authors[7].Account).Archived, "removed author state");

        const string testFeed = "https://feeds.example.com/xueqiu/user/1556808774";
        var configuredAuthor = first with { FeedUrl = testFeed };
        var feedSave = new WatchRequest("author-save", Author: configuredAuthor, ChangeId: Guid.NewGuid());
        Require((await Send(feedSave)).Error is null && (await Send(feedSave)).Error is null,
            "per-author RSS write and exact replay");
        var feedPage = (await Send(new("leads"))).Leads!;
        Require(feedPage is { Configured: true, ConfiguredFeeds: 1 } &&
            feedPage.UnconfiguredFeeds == authors.Length - 2 &&
            feedPage.Authors.Single(x => x.Account == first.Account).FeedUrl == testFeed,
            "per-author RSS read-back and archived author exclusion");
        Console.WriteLine("PASS 作者独立 RSS 配置通过私有管道保存、读回并去重。");
        Require((await Send(new("alert-subscription", new(Account: first.Account)))).Alert is
            { State: GoogleAlertState.External }, "manual RSS replaces the pending create intent");

        var stock = new Security("沪市", "600001", "合成样本", "样例行业");
        var candidate = WatchInput.Candidates("$合成样本(SH600001)$ 以及 HK00700", [stock]);
        Require(candidate.Count == 2 && candidate[0] == stock, "explicit stock candidates");
        var now = WatchProtocol.BeijingNow.AddHours(-1);
        var old = new Opinion(Guid.NewGuid(), first.Account, stock, now.AddDays(-2), Stance.Bullish,
            Attribution.Author, "样本观点：订单增长。", "来源样本", $"https://xueqiu.com/{first.Account}/111", Question: "订单何时兑现？");
        var reversal = old with { Id = Guid.NewGuid(), PublishedAt = now, Stance = Stance.Bearish,
            Url = $"https://xueqiu.com/{first.Account}/112" };
        var positive = old with { Id = Guid.NewGuid(), Account = second.Account, PublishedAt = now,
            Url = $"https://xueqiu.com/{second.Account}/122" };
        var repost = positive with { Id = Guid.NewGuid(), Stance = Stance.Pending, Attribution = Attribution.Repost,
            Url = $"https://xueqiu.com/{second.Account}/123" };
        var unresolved = positive with { Id = Guid.NewGuid(), Security = null, Stance = Stance.Pending, Url = "" };
        foreach (var opinion in new[] { old, reversal, positive, repost, unresolved })
        {
            var request = new WatchRequest("opinion-save", Opinion: opinion, ChangeId: Guid.NewGuid());
            Require((await Send(request)).Error is null, "save opinion");
            Require((await Send(request)).Error is null, "repeat command");
        }

        var duplicate = await Send(new("opinion-save", Opinion: old with { Id = Guid.NewGuid() }, ChangeId: Guid.NewGuid()));
        Require(duplicate.Error is not null, "duplicate source rejected");
        var overview = (await Send(new("overview"))).Overview!;
        Require(overview.Stocks is [{ Bullish: 1, Bearish: 1, Discussions: 3 }] && overview.Unmatched == 1, "matrix counts");
        var detail = (await Send(new("detail", new(Security: stock)))).Detail!;
        Require(detail.Total == 2 && detail.Rows.Any(x => x.Id == reversal.Id), "current detail matches matrix");
        Require((await Send(new("detail", new(Security: stock, Mode: "timeline")))).Detail!.Total == 4, "timeline includes history");
        Require((await Send(new("detail", new(Mode: "pending")))).Detail!.Total == 1, "unresolved record stays reviewable");
        Require((await Send(new("detail", new(Mode: "questions")))).Detail!.Total >= 1, "research questions use saved opinions");

        var changed = detail.Rows.Single(x => x.Id == reversal.Id) with { Stance = Stance.Bullish };
        Require((await Send(new("opinion-save", Opinion: changed, ChangeId: Guid.NewGuid()))).Error is null, "correct saved opinion");
        Require((await Send(new("opinion-save", Opinion: changed, ChangeId: Guid.NewGuid()))).Error is not null, "reject stale revision");
        Require((await Send(new("overview"))).Overview!.Stocks is [{ Bullish: 2, Bearish: 0 }], "corrected matrix");
        await XueqiuPostBodyChecks.RunAsync(Send, first.Account, $"https://xueqiu.com/{first.Account}/181");

        var database = Path.Combine(data, "plugins", WatchProtocol.PluginId, "opinions.db");
        using var stream = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[16];
        stream.ReadExactly(header);
        Require(!header.AsSpan().SequenceEqual("SQLite format 3\0"u8), "private database uses encrypted pages");
        Console.WriteLine("PASS 雪球观点追踪：私有管道、独立加密库、作者配置、识别、幂等、统计、历史、待确认及纠正。");
    }

    private static void Require(bool success, string check)
    {
        if (!success) throw new InvalidOperationException($"雪球观点追踪验证失败：{check}");
    }
}
