using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Playwright;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal interface IOriginalPageReader : IAsyncDisposable
{
    Task<OriginalPageRead> ReadAsync(OriginalPageLead post, CancellationToken token);
}

/// <summary>只读取正常返回的公开原帖 HTML，浏览器不执行网页脚本。</summary>
internal sealed class OriginalPageBrowser(IPlaywright driver, IBrowser browser, IBrowserContext context,
    CancellationTokenRegistration cancel) : IOriginalPageReader
{
    private static readonly Regex MoreText = new(
        @"^(?:展开全文|阅读全文|查看全文|登录后查看全文|打开雪球(?:\s*App)?查看全文)\s*[>＞]?$");
    private static readonly Regex VerificationTitle = new(
        @"^(?:安全验证|访问验证|人机验证|Access Denied|Just a moment)(?:[\s.…!！-]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private const int MaximumHtmlBytes = 2 * 1024 * 1024;

    internal static BrowserNewContextOptions StaticContextOptions() => new()
    {
        JavaScriptEnabled = false, AcceptDownloads = false, ServiceWorkers = ServiceWorkerPolicy.Block, Locale = "zh-CN"
    };

    internal static async Task<OriginalPageBrowser> OpenAsync(IWebProxy? route, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var driver = await Playwright.CreateAsync();
        // 任务取消时关闭驱动，让仍在等待的 Playwright 调用退出。
        var cancellation = token.Register(driver.Dispose);
        try
        {
            var browser = await driver.Chromium.LaunchAsync(new()
            {
                Channel = "chrome", Headless = true, ChromiumSandbox = true, Timeout = 12_000,
                Args = route is null ? ["--no-proxy-server"] : [],
                Proxy = route is null ? null : BrowserProxy(route)
            });
            var context = await browser.NewContextAsync(StaticContextOptions());
            return new(driver, browser, context, cancellation);
        }
        catch
        {
            cancellation.Dispose();
            driver.Dispose();
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static Proxy BrowserProxy(IWebProxy route)
    {
        var endpoint = route.GetProxy(new Uri("https://www.xueqiu.com/"));
        if (endpoint is null || !endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.UserInfo.Length != 0 || endpoint.PathAndQuery != "/" || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("原帖浏览器需要有效的本机 VPN 代理。");
        // 浏览器与同一任务的 RSS 请求共用 VPN 节点，代理凭据仅交给浏览器会话。
        var credential = route.Credentials?.GetCredential(endpoint, "Basic");
        return new() { Server = endpoint.AbsoluteUri, Username = credential?.UserName, Password = credential?.Password };
    }

    public async Task<OriginalPageRead> ReadAsync(OriginalPageLead post, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var expected = WatchInput.AlertSourceLink(post.Url, post.Account);
        var target = new UriBuilder(expected) { Host = "www.xueqiu.com" }.Uri.AbsoluteUri;
        var page = await context.NewPageAsync();
        var read = new OriginalPageRead("Unavailable");
        try
        {
            page.SetDefaultTimeout(1500);
            // 让 Chrome 正常发出文档请求，同时在任何重定向或子资源请求前检查地址。
            var network = await context.NewCDPSessionAsync(page);
            network.Event("Fetch.requestPaused").OnEvent += (sender, details) =>
            {
                if (details is { } request) _ = ContinuePageRequestAsync(request);
            };
            await network.SendAsync("Fetch.enable");
            var response = await page.GotoAsync(target, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 12_000 });
            token.ThrowIfCancellationRequested();
            if (read.State != "Read" || response is null) return read;
            return (await response.BodyAsync()).Length <= MaximumHtmlBytes ? await ExtractAsync(page, post) : new("BodyUnavailable");

            async Task ContinuePageRequestAsync(JsonElement request)
            {
                var arguments = new Dictionary<string, object> { ["requestId"] = request.GetProperty("requestId").GetString()! };
                var isDocument = request.GetProperty("resourceType").GetString() == "Document" &&
                    request.GetProperty("request").GetProperty("url").GetString() == target;
                try
                {
                    if (isDocument && request.TryGetProperty("responseStatusCode", out var status))
                    {
                        var headers = (request.TryGetProperty("responseHeaders", out var rawHeaders)
                            ? rawHeaders.EnumerateArray().ToArray() : [])
                            .ToLookup(header => header.GetProperty("name").GetString()!,
                                header => header.GetProperty("value").GetString()!, StringComparer.OrdinalIgnoreCase);
                        read = ResponseState(status.GetInt32(), name => headers[name].FirstOrDefault());
                        if (read.State == "Read")
                        {
                            await network.SendAsync("Fetch.continueResponse", arguments);
                            return;
                        }
                    }
                    else if (isDocument && !request.TryGetProperty("responseErrorReason", out _))
                    {
                        arguments["interceptResponse"] = true;
                        await network.SendAsync("Fetch.continueRequest", arguments);
                        return;
                    }
                    arguments["errorReason"] = "BlockedByClient";
                    await network.SendAsync("Fetch.failRequest", arguments);
                }
                catch (PlaywrightException)
                {
                    if (isDocument) read = new("Unavailable");
                }
            }
        }
        catch (PlaywrightException)
        {
            token.ThrowIfCancellationRequested();
            return read.State == "Read" ? new("Unavailable") : read;
        }
        finally
        {
            if (!token.IsCancellationRequested)
                try { await page.CloseAsync(); } catch (PlaywrightException) { }
        }
    }

    private static OriginalPageRead ResponseState(int status, Func<string, string?> readHeader)
    {
        // Retry-After 可以是秒数或绝对时间。缺失时等待 30 分钟，有效值限定在 1 分钟至 1 天。
        if (status == 429)
        {
            var delay = TimeSpan.FromMinutes(30);
            if (readHeader("retry-after") is { } value)
            {
                if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 60, 86400));
                else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
                    delay = TimeSpan.FromSeconds(Math.Clamp((until - DateTimeOffset.UtcNow).TotalSeconds, 60, 86400));
            }
            return new("RateLimited", RetryAfter: delay);
        }
        if (status is >= 300 and < 400 or 401 or 403 or 418 or 432 or 446 or 451)
            return new("AccessRestricted");
        if (status == 404 || status == 410) return new("BodyUnavailable");
        if (status != 200) return new("Unavailable");
        if (long.TryParse(readHeader("content-length"), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) &&
            bytes > MaximumHtmlBytes) return new("BodyUnavailable");
        return readHeader("content-type")?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
            ? new("Read") : new("BodyUnavailable");
    }

    internal static async Task<OriginalPageRead> ExtractAsync(IPage page, OriginalPageLead post)
    {
        // 同时用页面地址、作者 ID 和永久链接核对同一条线索。正文选择器只覆盖发言，排除评论区。
        var owner = page.Locator(".article__author a.name[data-tooltip]");
        var permalink = page.Locator(".article__author a.time[href]");
        var body = page.Locator("article.article__bd .article__bd__detail:visible");
        if (await owner.CountAsync() != 1 || await permalink.CountAsync() != 1 || await body.CountAsync() != 1)
            return new(VerificationTitle.IsMatch(await page.TitleAsync()) ? "AccessRestricted" : "BodyUnavailable");
        if (await owner.GetAttributeAsync("data-tooltip") != post.Account ||
            !Uri.TryCreate(new Uri(page.Url), await permalink.GetAttributeAsync("href"), out var identity))
            return new("BodyUnavailable");
        try
        {
            if (WatchInput.AlertSourceLink(page.Url, post.Account) != post.Url ||
                WatchInput.AlertSourceLink(identity.AbsoluteUri, post.Account) != post.Url)
                return new("BodyUnavailable");
        }
        catch (ArgumentException) { return new("BodyUnavailable"); }

        var time = permalink.Locator("time[itemprop='datePublished'][datetime]");
        if (await time.CountAsync() != 1 || !DateTimeOffset.TryParse(await time.GetAttributeAsync("datetime"),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published))
            return new("BodyUnavailable");
        var text = (await body.InnerTextAsync()).Trim();
        if (text.Length == 0 || await body.GetByText(MoreText).CountAsync() != 0)
            return new("BodyUnavailable");
        return new("FullText", text, published);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var closing = browser.CloseAsync();
            await closing.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException) { }
        finally
        {
            cancel.Dispose();
            driver.Dispose();
        }
    }
}
