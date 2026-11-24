using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Playwright;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed partial class GoogleAlertsPageSession(
    IPlaywright playwright, IBrowserContext context, IPage page) : IAsyncDisposable
{
    private const string Address = "https://www.google.com.hk/alerts?hl=zh-CN";
    private ILocator AccountButton => page.GetByRole(AriaRole.Button, new() { NameRegex = AccountCaption() });
    private ILocator SearchBox => page.Locator(".query_div input[placeholder]:visible");
    private ILocator AlertRows => page.Locator("#manage-alerts-div li.alert_instance[data-id]");

    internal static async Task<GoogleAlertsPageSession> OpenAsync(bool interactive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        IBrowserContext? context = null;
        try
        {
            // 专用 Chrome 配置承载 Google 登录态，CRS 通过网页校验账号身份。
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CRS", "GoogleAlertsBrowser");
            context = await playwright.Chromium.LaunchPersistentContextAsync(directory, new()
            {
                Channel = "chrome", Headless = !interactive, ChromiumSandbox = true,
                Locale = "zh-CN", Timeout = 20_000
            }).ConfigureAwait(false);
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
            page.SetDefaultTimeout(8_000);
            await page.GotoAsync(Address, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 })
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

    internal async Task<string> ReadAccountAsync(bool waitForLogin, CancellationToken token)
    {
        if (waitForLogin)
            await AccountButton.WaitForAsync(new() { Timeout = 120_000 }).WaitAsync(token).ConfigureAwait(false);
        VerifyAlertsPage();
        if (await AccountButton.CountAsync().ConfigureAwait(false) != 1)
            throw new ArgumentException("Google 快讯需要重新连接。");
        var caption = await AccountButton.GetAttributeAsync("aria-label").ConfigureAwait(false) ?? "";
        var address = AccountAddress().Match(caption);
        if (!address.Success) throw new ArgumentException("无法确认当前 Google 账号。");
        return Fingerprint(address.Groups["account"].Value.ToLowerInvariant());
    }

    internal async Task<GoogleWebAlert?> FindByQueryAsync(string query, CancellationToken token)
    {
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

    internal async Task<GoogleWebAlert> CreateRssAsync(string query, string account, CancellationToken token)
    {
        if (await FindByQueryAsync(query, token).ConfigureAwait(false) is not null)
            throw new ArgumentException("Google 快讯已有相同查询，已停止重复创建。");
        await SearchBox.FillAsync(query).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { NameRegex = ShowOptions() }).ClickAsync().ConfigureAwait(false);
        var delivery = page.GetByRole(AriaRole.Row, new() { NameRegex = DeliveryCaption() }).GetByRole(AriaRole.Listbox);
        await delivery.ClickAsync().ConfigureAwait(false);
        await page.GetByRole(AriaRole.Menuitem, new() { NameRegex = RssCaption() }).ClickAsync().ConfigureAwait(false);
        if (!(await delivery.InnerTextAsync().ConfigureAwait(false)).Contains("RSS", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Google 快讯未确认选择 RSS 投递。");
        await VerifyAccountAsync(account, token).ConfigureAwait(false);
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
        throw new ArgumentException("尚未核实 Google 快讯创建结果，请重新同步。");
    }

    internal async Task DeleteAsync(string remoteId, string account, CancellationToken token)
    {
        await VerifyAccountAsync(account, token).ConfigureAwait(false);
        // 依据 CRS 保存的远端标识定位原提醒，避免误删同查询的外部提醒。
        var row = await FindByIdAsync(remoteId, token).ConfigureAwait(false);
        if (row is null) return;
        await VerifyAccountAsync(account, token).ConfigureAwait(false);
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

    private void VerifyAlertsPage()
    {
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            url.Host is not ("www.google.com.hk" or "www.google.com") ||
            url.AbsolutePath.TrimEnd('/') is not ("/alerts" or "/alerts/manage"))
            throw new ArgumentException("未处在已登录的 Google 快讯管理页面。");
    }

    private async Task VerifyAccountAsync(string account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (await ReadAccountAsync(false, token).ConfigureAwait(false) != account)
            throw new ArgumentException("Google 账号发生变化，已停止同步。");
    }

    private static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [GeneratedRegex(@"^Google\s*(?:账号|帐号|帳號|Account)")]
    private static partial Regex AccountCaption();
    [GeneratedRegex(@"\((?<account>[^()\s]+@[^()\s]+)\)\s*$")]
    private static partial Regex AccountAddress();
    [GeneratedRegex(@"^(?:显示选项|顯示選項|Show options)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowOptions();
    [GeneratedRegex(@"^(?:发送到|傳送至|Deliver to)", RegexOptions.IgnoreCase)]
    private static partial Regex DeliveryCaption();
    [GeneratedRegex(@"^(?:RSS Feed|RSS)$", RegexOptions.IgnoreCase)]
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
