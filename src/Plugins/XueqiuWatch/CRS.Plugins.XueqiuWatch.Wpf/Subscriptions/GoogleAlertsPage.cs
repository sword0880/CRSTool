using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Playwright;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class GoogleAlertsPage(IPage page)
{
    private const string RowSelector = ".alert_instance .delivery_settings_view";
    private const string FeedSelector = "a:has(.rss_icon)";
    private const string SignedInSelector = "#gb a[href*='/SignOutOptions']";

    internal async Task<string> ApplyAsync(LeadSubscription state, CancellationToken token)
    {
        EnsureAlertsPage();
        if (!await page.Locator(SignedInSelector).IsVisibleAsync())
            throw new GoogleAlertsException("请连接 Google 并登录创建当前订阅时使用的账号。");

        var rows = await ReadRowsAsync();
        var matching = rows.Where(row => SameQuery(row.Query, state.Query) &&
            row.Url is not null && IsSameOwner(row.Url, state)).ToArray();
        var current = rows.SingleOrDefault(row => row.Url is not null &&
            LeadSubscription.Fingerprint(row.Url) == state.FeedFingerprint);
        if (current?.Url is { } currentUrl && SameQuery(current.Query, state.Query)) return currentUrl;
        if (matching.Length > 1) throw new GoogleAlertsException("Google 有多条相同订阅，请确认后再同步。");
        if (current is null && matching.Length == 1) return matching[0].Url!;
        if (current is null && state.FeedFingerprint is not null)
            throw new GoogleAlertsException("当前 Google 账号下找不到 CRS 已保存的订阅，请确认登录账号。");
        if (rows.Any(row => SameQuery(row.Query, state.Query)))
            throw new GoogleAlertsException("Google 已存在这些关键词的快讯，请检查现有快讯的投递设置。");

        token.ThrowIfCancellationRequested();
        if (current is not null)
        {
            // 按 RSS 地址定位已有快讯，避免修改同一账号下的其他快讯。
            var target = page.Locator(RowSelector).Filter(new()
            {
                Has = page.Locator($"{FeedSelector}[href$='{new Uri(current.Url!).AbsolutePath}']")
            });
            if (await target.CountAsync() != 1)
                throw new GoogleAlertsException("Google 上的订阅已变化，请重新同步。");
            await target.Locator(".edit_button").ClickAsync();
        }

        var input = page.Locator("#search_box input[type=text]:visible");
        await input.FillAsync(state.Query);
        await input.PressAsync("End");
        var options = page.Locator("#search_box .show_options:visible");
        if (await options.IsVisibleAsync()) await options.ClickAsync();
        await SelectAsync(".delivery_select [role=listbox]:visible", "RSS Feed");
        await SelectAsync(".volume_select[role=listbox]:visible", "All results", "所有结果");
        if (await input.InputValueAsync() != state.Query)
            throw new GoogleAlertsException("Google 未保留完整的观察名单查询。");

        token.ThrowIfCancellationRequested();
        EnsureAlertsPage();
        await page.Locator(current is null ? "#create_alert:visible" : "#save_alert:visible").ClickAsync();

        // Google 完成更新并且页面出现新查询与 RSS 后才写回本地。刷新后可恢复已在 Google 成功的操作。
        await page.WaitForFunctionAsync("""
            query => Array.from(document.querySelectorAll('.alert_instance .delivery_settings_view')).some(row =>
                row.querySelector('span')?.textContent.trim() === query && row.querySelector('a:has(.rss_icon)'))
            """, state.Query, new() { Timeout = 10_000 });
        var confirmed = (await ReadRowsAsync()).Where(row => SameQuery(row.Query, state.Query) &&
            row.Url is not null && IsSameOwner(row.Url, state)).ToArray();
        if (confirmed.Length != 1)
            throw new GoogleAlertsException("Google 未返回唯一的 RSS 订阅，旧配置已保留。");
        return confirmed[0].Url!;
    }

    private async Task SelectAsync(string selector, string label, string? translated = null)
    {
        var list = page.Locator(selector);
        var selected = (await list.InnerTextAsync()).Trim();
        if (selected.Equals(label, StringComparison.OrdinalIgnoreCase) || selected == translated) return;
        await list.ClickAsync();
        var allowed = translated is null ? new[] { label } : new[] { label, translated };
        var labels = string.Join('|', allowed.Select(System.Text.RegularExpressions.Regex.Escape));
        var option = page.GetByText(new System.Text.RegularExpressions.Regex($"^\\s*(?:{labels})\\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Filter(new() { Visible = true });
        await option.ClickAsync();
        selected = (await list.InnerTextAsync()).Trim();
        if (!allowed.Contains(selected, StringComparer.OrdinalIgnoreCase))
            throw new GoogleAlertsException("Google 未确认快讯选项，无法安全保存订阅。");
    }

    private async Task<AlertRow[]> ReadRowsAsync()
    {
        var rows = await page.Locator(RowSelector).EvaluateAllAsync<string[][]>("""
            rows => rows.map(row => [row.querySelector('span')?.textContent.trim() || '',
                row.querySelector('a:has(.rss_icon)')?.href || ''])
            """);
        return rows.Select(row => new AlertRow(row[0], ValidFeed(row[1]))).ToArray();
    }

    private static bool IsSameOwner(string url, LeadSubscription state) =>
        state.OwnerFingerprint is null || LeadSubscription.OwnerOf(url) == state.OwnerFingerprint;

    private static string? ValidFeed(string url)
    {
        try { return WatchInput.GoogleAlertFeed(url); }
        catch (ArgumentException) { return null; }
    }

    private void EnsureAlertsPage()
    {
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            url.Host != "www.google.com" || !url.IsDefaultPort || url.AbsolutePath.TrimEnd('/') != "/alerts")
            throw new GoogleAlertsException("Google Alerts 未打开，请先连接 Google 或检查访问状态。");
    }

    private static bool SameQuery(string first, string second) => first.Split(" OR ", StringSplitOptions.TrimEntries)
        .Order(StringComparer.Ordinal).SequenceEqual(second.Split(" OR ", StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private sealed record AlertRow(string Query, string? Url);
}
