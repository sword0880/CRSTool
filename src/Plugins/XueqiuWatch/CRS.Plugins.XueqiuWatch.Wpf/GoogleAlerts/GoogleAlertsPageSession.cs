using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Playwright;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed partial class GoogleAlertsPageSession(
    IPlaywright playwright, IBrowserContext context, IPage page) : IAsyncDisposable
{
    // Google 账号入口可能是链接、按钮或自定义控件，不依赖单一 ARIA 角色。
    private ILocator AccountControls => page.Locator("[aria-label]:visible, a[title]:visible, button[title]:visible");
    private ILocator SearchBox => page.Locator(".query_div input:not([type=hidden]):visible, input#alert-query:visible, input#query:visible");
    internal string DiagnosticStage { get; private set; } = "登录状态检查";
    private ILocator AlertRows => page.Locator("#manage-alerts-div li.alert_instance[data-id]");

    internal static async Task<GoogleAlertsPageSession> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        IBrowserContext? context = null;
        try
        {
            // 登录由普通 Chrome 完成，同步只在已登录的快讯管理页执行。
            context = await playwright.Chromium.LaunchPersistentContextAsync(GoogleAlertsBrowserLogin.ProfileDirectory, new()
            {
                Channel = "chrome", Headless = true, ChromiumSandbox = true,
                Locale = "zh-CN", Timeout = 20_000
            }).ConfigureAwait(false);
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
            page.SetDefaultTimeout(8_000);
            await page.GotoAsync(GoogleAlertsBrowserLogin.AlertsAddress, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 })
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(playwright, context, page);
        }
        catch
        {
            try { if (context is not null) await context.CloseAsync().ConfigureAwait(false); }
            finally { playwright.Dispose(); }
            throw;
        }
    }

    internal async Task<string> ReadAccountAsync(CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(8);
        var state = "页面尚未就绪";
        var needsSignIn = false;
        do
        {
            token.ThrowIfCancellationRequested();
            if (IsAlertsPage())
            {
                var labels = await AccountControls.EvaluateAllAsync<string[]>(
                    "elements => elements.flatMap(e => [e.getAttribute('aria-label'), e.getAttribute('title')].filter(Boolean))")
                    .WaitAsync(token).ConfigureAwait(false);
                var account = GoogleAlertsAccountIdentity.ResolveFingerprint(labels);
                if (account is not null && await SearchBox.CountAsync().ConfigureAwait(false) == 1)
                    return account;
                needsSignIn = account is null;
                state = account is null ? "未识别唯一 Google 账号（未登录、多个账号入口或页面改版）" : "已识别账号，但快讯搜索框尚未就绪";
            }
            else if (Uri.TryCreate(page.Url, UriKind.Absolute, out var url) &&
                url.Scheme == "https" && url.Host == "accounts.google.com")
            {
                needsSignIn = true;
                state = "Google 登录或账号选择/验证页面";
            }
            else
            {
                needsSignIn = false;
                state = "未进入 Google 快讯管理页面（可能是网络、重定向或同意页面）";
            }
            if (elapsed.Elapsed >= limit) break;
            await Task.Delay(300, token).ConfigureAwait(false);
        } while (elapsed.Elapsed < limit);
        // 不输出原始 URL、账号、DOM、Cookie 或 Playwright 调用日志。
        if (needsSignIn)
            throw new GoogleAlertsSignInRequiredException($"Google 快讯账号未就绪（{state}）。请从观察名单的「更多」打开 Google 登录，登录后关闭专用 Chrome。");
        throw new ArgumentException($"Google 快讯登录检查超时（{state}）。请在普通 Chrome 中核对 Google 快讯能否打开。");
    }

    internal async Task<GoogleWebAlert?> FindByQueryAsync(string query, CancellationToken token)
    {
        DiagnosticStage = "读取快讯列表";
        GoogleWebAlert? match = null;
        foreach (var row in await ReadRowsAsync(token).ConfigureAwait(false))
        {
            var text = await row.GetByText(query, new() { Exact = true }).CountAsync().ConfigureAwait(false);
            if (text == 0) continue;
            if (match is not null || text != 1)
                throw new ArgumentException("同一查询存在多条 Google 快讯，请在网页核对。");
            match = await ReadRowAsync(row).ConfigureAwait(false);
        }
        return match;
    }

    internal async Task<GoogleWebAlert?> RecheckByQueryAsync(string query, string account, CancellationToken token)
    {
        await ReloadAsync(account, token).ConfigureAwait(false);
        return await FindByQueryAsync(query, token).ConfigureAwait(false);
    }

    internal async Task ReloadAsync(string account, CancellationToken token)
    {
        DiagnosticStage = "重新加载快讯列表";
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 })
            .WaitAsync(token).ConfigureAwait(false);
        await VerifyAccountAsync(account, token).ConfigureAwait(false);
    }

    internal async Task PrepareRssAsync(string query, string account, CancellationToken token)
    {
        if (await FindByQueryAsync(query, token).ConfigureAwait(false) is not null)
            throw new ArgumentException("Google 快讯已有相同查询，已停止重复创建。");
        DiagnosticStage = "填写作者查询";
        // Google 快讯需要键盘事件来展开创建区，单独设置输入值不会触发。
        await SearchBox.PressSequentiallyAsync(query, new() { Delay = 20 }).WaitAsync(token).ConfigureAwait(false);
        DiagnosticStage = "打开投递选项";
        await page.GetByRole(AriaRole.Button, new() { NameRegex = ShowOptions() })
            .Or(page.GetByRole(AriaRole.Link, new() { NameRegex = ShowOptions() })).ClickAsync().ConfigureAwait(false);
        var delivery = page.GetByRole(AriaRole.Row, new() { NameRegex = DeliveryCaption() }).GetByRole(AriaRole.Listbox);
        DiagnosticStage = "选择 RSS 投递";
        await delivery.ClickAsync().ConfigureAwait(false);
        await page.GetByRole(AriaRole.Menuitem, new() { NameRegex = RssCaption() })
            .Or(page.GetByRole(AriaRole.Option, new() { NameRegex = RssCaption() })).ClickAsync().ConfigureAwait(false);
        if (!(await delivery.InnerTextAsync().ConfigureAwait(false)).Contains("RSS", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Google 快讯未确认选择 RSS 投递。");
        await VerifyAccountAsync(account, token).ConfigureAwait(false);
        DiagnosticStage = "确认创建按钮可用";
        await page.Locator("#create_alert:visible").ClickAsync(new() { Trial = true }).WaitAsync(token).ConfigureAwait(false);
    }

    internal async Task<GoogleWebAlert> SubmitRssAsync(string query, string account, CancellationToken token)
    {
        await VerifyAccountAsync(account, token).ConfigureAwait(false);
        DiagnosticStage = "创建快讯（结果未确认时不得重复创建）";
        await page.Locator("#create_alert:visible").ClickAsync().ConfigureAwait(false);
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(350, token).ConfigureAwait(false);
            await VerifyAccountAsync(account, token).ConfigureAwait(false);
            if (!await SearchBox.IsVisibleAsync().ConfigureAwait(false) ||
                (await SearchBox.InputValueAsync().ConfigureAwait(false)).Length != 0) continue;
            if (await FindByQueryAsync(query, token).ConfigureAwait(false) is { FeedUrl.Length: > 0 } alert)
                return alert;
        }
        if (await RecheckByQueryAsync(query, account, token).ConfigureAwait(false) is { FeedUrl.Length: > 0 } confirmed)
            return confirmed;
        throw new ArgumentException("尚未核实 Google 快讯创建结果，请重新同步。");
    }

    internal async Task DeleteAsync(string remoteId, string account, CancellationToken token)
    {
        await VerifyAccountAsync(account, token).ConfigureAwait(false);
        // 依据 CRS 保存的远端标识定位原提醒，避免误删同查询的外部提醒。
        var row = await FindByIdAsync(remoteId, token).ConfigureAwait(false);
        if (row is null) return;
        await VerifyAccountAsync(account, token).ConfigureAwait(false);
        DiagnosticStage = "删除快讯（结果未确认时需重新核对）";
        await row.Locator(".delete_button:visible").ClickAsync().ConfigureAwait(false);
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(350, token).ConfigureAwait(false);
            await VerifyAccountAsync(account, token).ConfigureAwait(false);
            if (await FindByIdAsync(remoteId, token).ConfigureAwait(false) is null) return;
        }
        throw new ArgumentException("尚未核实 Google 快讯删除结果，请重新同步。");
    }

    private async Task<ILocator?> FindByIdAsync(string id, CancellationToken token)
    {
        ILocator? match = null;
        foreach (var row in await ReadRowsAsync(token).ConfigureAwait(false))
        {
            var rawId = await row.GetAttributeAsync("data-id").ConfigureAwait(false);
            if (rawId is null || Fingerprint(rawId) != id) continue;
            if (match is not null) throw new ArgumentException("Google 快讯标识重复，无法安全删除。");
            if (!SafeRowId().IsMatch(rawId)) throw new ArgumentException("Google 快讯标识格式已变化。");
            match = page.Locator($"#manage-alerts-div li.alert_instance[data-id=\"{rawId}\"]:visible");
        }
        return match;
    }

    private async Task<IReadOnlyList<ILocator>> ReadRowsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        VerifyAlertsPage();
        await page.Locator("#manage-alerts-div").WaitForAsync(new() { State = WaitForSelectorState.Attached })
            .ConfigureAwait(false);
        if ((await SearchBox.InputValueAsync().ConfigureAwait(false)).Length != 0)
            throw new ArgumentException("Google 快讯列表仍在查询预览中。");
        var rows = await page.Locator("#manage-alerts-div li.alert_instance[data-id]:visible").AllAsync()
            .ConfigureAwait(false);
        if (rows.Count == 0 && await AlertRows.CountAsync().ConfigureAwait(false) != 0)
            throw new ArgumentException("Google 快讯列表尚未显示。");
        return rows;
    }

    private async Task<GoogleWebAlert> ReadRowAsync(ILocator row)
    {
        var link = row.Locator("a[href*='/alerts/feeds/']");
        var href = await link.CountAsync().ConfigureAwait(false) == 1 ?
            await link.GetAttributeAsync("href").ConfigureAwait(false) : null;
        var url = href is not null && Uri.TryCreate(new Uri(page.Url), href, out var feed) &&
            WatchInput.IsGoogleAlertsFeed(feed) ? feed.AbsoluteUri : "";
        return new(await ReadRowIdAsync(row).ConfigureAwait(false), url);
    }

    private static async Task<string> ReadRowIdAsync(ILocator row)
    {
        var id = await row.GetAttributeAsync("data-id").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("无法核实 Google 快讯标识。");
        return Fingerprint(id);
    }

    private bool IsAlertsPage() =>
        Uri.TryCreate(page.Url, UriKind.Absolute, out var url) && url.Scheme == "https" &&
        url.Host is ("www.google.com.hk" or "www.google.com") &&
        Regex.IsMatch(url.AbsolutePath.TrimEnd('/'), @"^/(?:u/\d+/)?alerts(?:/u/\d+)?(?:/manage)?$");

    private void VerifyAlertsPage()
    {
        if (!IsAlertsPage()) throw new ArgumentException("未处在已登录的 Google 快讯管理页面。");
    }

    internal async Task VerifyAccountAsync(string account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (await ReadAccountAsync(token).ConfigureAwait(false) != account)
            throw new ArgumentException("Google 账号发生变化，已停止同步。");
    }

    private static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [GeneratedRegex(@"^(?:显示选项|顯示選項|顯示選項設定|Show options)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowOptions();
    [GeneratedRegex(@"^(?:发送到|发送至|傳送至|傳送到|投遞至|Deliver to)", RegexOptions.IgnoreCase)]
    private static partial Regex DeliveryCaption();
    [GeneratedRegex(@"^(?:RSS Feed|RSS 供稿|RSS 資訊提供|RSS)$", RegexOptions.IgnoreCase)]
    private static partial Regex RssCaption();
    [GeneratedRegex(@"^[a-zA-Z0-9:._-]+$")]
    private static partial Regex SafeRowId();

    public async ValueTask DisposeAsync()
    {
        try { await context.CloseAsync().ConfigureAwait(false); }
        finally { playwright.Dispose(); }
    }
}

internal sealed record GoogleWebAlert(string Id, string FeedUrl);
