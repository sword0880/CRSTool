using System.Security.Cryptography;
using CRS.Feed;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Plugins.XueqiuWatch.Jobs;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using Microsoft.Playwright;

internal static class XueqiuOriginalPageChecks
{
    private static OriginalPageLead PublicPost =>
        new("9976484464", "https://xueqiu.com/9976484464/413882047", 0, 0);

    internal static async Task RunFixtureAsync()
    {
        const string url = "https://www.xueqiu.com/123456/987654";
        var post = new OriginalPageLead("123456", "https://xueqiu.com/123456/987654", 0, 0);
        var html = """
            <!doctype html><meta charset="utf-8"><title>公开样本</title>
            <div class="article__author"><a class="name" data-tooltip="123456">测试作者</a>
              <a class="time" href="/123456/987654">发布于
                <time itemprop="datePublished" datetime="2026-11-23T01:02:00Z">09:02</time>
              </a>
            </div>
            <article class="article__bd"><div class="article__bd__detail"><p>第一段。</p><p>第二段。</p></div></article>
            <aside>全部讨论</aside>
            <script>document.querySelector('.article__bd__detail').textContent = '不应执行 JavaScript';</script>
            """;
        using var driver = await Playwright.CreateAsync();
        await using var browser = await driver.Chromium.LaunchAsync(new() { Channel = "chrome", Headless = true, ChromiumSandbox = true });
        await using var context = await browser.NewContextAsync(OriginalPageBrowser.StaticContextOptions());
        await context.RouteAsync("**/*", route => route.Request.Url == url ?
            route.FulfillAsync(new() { Body = html, Status = 200, ContentType = "text/html; charset=utf-8" }) : route.AbortAsync());
        var page = await context.NewPageAsync();
        await page.GotoAsync(url);
        var result = await OriginalPageBrowser.ExtractAsync(page, post);
        if (result.State != "FullText" || result.Text is not { } text || !text.Contains("第一段。") ||
            !text.Contains('\n') || !text.Contains("第二段。") || text.Contains("全部讨论") ||
            text.Contains("不应执行 JavaScript") || result.Published != DateTimeOffset.Parse("2026-11-23T01:02:00Z"))
            throw new InvalidOperationException("静态原帖提取结果不符合预期。");
        html = html.Replace("<p>第二段。</p>", "<button>展开全文</button>", StringComparison.Ordinal);
        await page.ReloadAsync();
        if ((await OriginalPageBrowser.ExtractAsync(page, post)).State != "BodyUnavailable")
            throw new InvalidOperationException("截断页面被标为全文。");
        html = html.Replace("data-tooltip=\"123456\"", "data-tooltip=\"654321\"", StringComparison.Ordinal)
            .Replace("<button>展开全文</button>", "<p>第二段。</p>", StringComparison.Ordinal);
        await page.ReloadAsync();
        if ((await OriginalPageBrowser.ExtractAsync(page, post)).State != "BodyUnavailable")
            throw new InvalidOperationException("其他作者的原帖被标为全文。");
        Console.WriteLine("PASS Chrome 禁用 JavaScript 后提取静态正文和原帖时间，保留段落，拒绝截断和作者不符。");
    }

    internal static async Task RunLiveAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var browser = await OriginalPageBrowser.OpenAsync(null, deadline.Token);
        var result = await browser.ReadAsync(PublicPost, deadline.Token);
        if (result.State != "FullText" || result is not { Text.Length: > 1000, Published: not null } ||
            result.Text.Contains("全部讨论", StringComparison.Ordinal))
            throw new InvalidOperationException("公开原帖静态读取结果：" + result.State);
        Console.WriteLine($"PASS 公开原帖通过 Chrome 静态读取并核对身份，正文 {result.Text.Length} 字符。");
    }

    internal static async Task RunWorkerAsync(BackgroundPipeClient client, string data, CancellationToken token)
    {
        const string source = "https://www.google.com/alerts/feeds/0/0";
        var post = PublicPost;
        var addAuthor = new WatchRequest("author-save", Author: new(post.Account, "原帖样本", FeedUrl: source), ChangeId: Guid.NewGuid());
        var authorReply = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), WorkerProtocol.PrivatePluginData,
            WatchProtocol.PluginId, Payload: WatchProtocol.Encode(addAuthor)), token);
        if (authorReply.Code != "Ok" || WatchProtocol.Decode<WatchReply>(authorReply.Payload!).Error is not null)
            throw new InvalidOperationException("未能写入隔离作者。");

        // 向本次测试创建的加密数据库投递模拟 Google 线索，避免依赖任一实际 Google 订阅。
        var master = ServiceKey.OpenExisting(data);
        var context = new WorkerPluginContext(WatchProtocol.PluginId, data, master);
        var key = context.OpenDataKey();
        try
        {
            using var store = new WatchStore(context.DataDirectory, key);
            var feed = new FeedDocument(null, null, null, new(source),
                [new("fixture", "公开样本", new(post.Url), "模拟概览", null, null, null, null, [])]);
            store.SaveLeadScans([new(post.Account, source, new(FeedFetchStatus.Updated, feed))],
                WatchProtocol.BeijingNow, token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(master);
        }

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
                if (record.Checkpoint?.Contains("核对原帖 1 条", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("隔离任务没有成功打开原帖浏览器：" + (record.Checkpoint ?? record.Result));
                Console.WriteLine(record.Checkpoint.Contains("取得全文 1 条", StringComparison.Ordinal)
                    ? "PASS 受监督 Worker 能打开 Chrome，并保存公开原帖全文。"
                    : "PASS 受监督 Worker 能打开 Chrome，公开原帖未取得全文时保留概览。");
                return;
            }
            await Task.Delay(300, token);
        }
    }
}
