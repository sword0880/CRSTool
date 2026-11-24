using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class AuthorEditor : WatchEditor
{
    private readonly WorkerWatchClient client;
    private readonly CancellationToken lifetime;
    private readonly ComboBox options = new() { DisplayMemberPath = nameof(AuthorChoice.Label) };
    private readonly TextBox account = new() { MaxLength = 200 };
    private readonly TextBox name = new() { MaxLength = 40 };
    private readonly TextBox note = new() { MaxLength = 80 };
    private readonly TextBox feedUrl = new() { MaxLength = 1024 };
    private readonly TextBox fullTextFeed = new() { MaxLength = 1024 };
    private readonly CheckBox active = new() { Content = "启用跟踪", IsChecked = true, Margin = new(0, 8, 0, 3) };
    private readonly TextBlock subscription = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 4) };
    private Author? selected;
    private readonly Button remove;

    public AuthorEditor(Window? owner, Author[] authors, WorkerWatchClient client, CancellationToken lifetime)
        : base(owner, "雪球观察名单", 650)
    {
        this.client = client;
        this.lifetime = lifetime;
        Field("作者", options);
        Field("雪球账号 ID 或主页链接", account);
        Field("显示名称", name);
        Field("备注", note);
        Field("Google Alerts RSS 地址", feedUrl);
        Field("获授权的全文 Feed 地址（选填）", fullTextFeed);
        fullTextFeed.ToolTip = "接受提供原帖链接、发布时间及完整纯文本的 JSON Feed 或 Atom。";
        Fields.Children.Add(new TextBlock
        {
            Text = "可使用已有 RSS 地址。手动订阅时，复制作者的搜索条件，在 Google Alerts 创建提醒并选择 RSS 投递，再粘贴生成的地址。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 6)
        });
        Fields.Children.Add(new TextBlock
        {
            Text = "搜索条件按雪球数字账号 ID 生成，可根据实际收录情况在 Google Alerts 中调整。RSS 地址包含订阅标识，请勿公开；CRS 将其保存在加密的观点数据库中。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6)
        });
        Fields.Children.Add(active);
        Fields.Children.Add(subscription);
        remove = AsyncAction("移除", () => SaveAsync(true));
        Finish(
            AsyncAction("打开 Google Alerts", OpenGoogleAlertsAsync),
            AsyncAction("复制搜索条件", CopySearchQueryAsync),
            AsyncAction("连接 Google 快讯", ConnectGoogleAlertsAsync),
            AsyncAction("同步 Google 快讯", SyncGoogleAlertsAsync),
            AsyncAction("停止网页同步", DisconnectGoogleAlertsAsync),
            AsyncAction("刷新", () => WithFeedbackAsync(async () => await RefreshAsync(null), lifetime)),
            remove,
            AsyncAction("保存", () => SaveAsync(false)),
            Action("关闭", Close));
        options.SelectionChanged += (_, _) => SelectAuthor((options.SelectedItem as AuthorChoice)?.Author);
        SetAuthors(authors, null);
    }

    private void SetAuthors(Author[] authors, string? selection)
    {
        var choices = new[] { new AuthorChoice("添加作者", null) }
            .Concat(authors.Select(a => new AuthorChoice(
                $"{a.Name} · {a.Account}" + (a.Archived ? "（已移除）" : a.Active ? "" : "（已暂停）"), a))).ToArray();
        options.ItemsSource = choices;
        options.SelectedItem = choices.FirstOrDefault(x => x.Author?.Account == selection) ?? choices[0];
    }

    private void SelectAuthor(Author? author)
    {
        selected = author;
        account.Text = author?.Account ?? "";
        account.IsReadOnly = author is not null;
        name.Text = author?.Name ?? "";
        note.Text = author?.Note ?? "";
        feedUrl.Text = author?.FeedUrl ?? "";
        fullTextFeed.Text = author?.FullTextFeedUrl ?? "";
        feedUrl.IsReadOnly = author?.AlertState is GoogleAlertState.Creating or GoogleAlertState.Managed or
            GoogleAlertState.PendingDelete or GoogleAlertState.Deleting;
        active.IsChecked = author is null || author.Archived || author.Active;
        remove.IsEnabled = author is { Archived: false };
        subscription.Text = author?.AlertState switch
        {
            GoogleAlertState.PendingCreate => "等待创建提醒。连接 Google 快讯后自动同步，也可绑定已有 RSS。",
            GoogleAlertState.Creating => "等待核对 Google Alerts 创建结果。",
            GoogleAlertState.Managed => "已绑定 CRS 托管提醒。",
            GoogleAlertState.External => "已绑定外部 RSS。移除作者只停止本地采集。",
            GoogleAlertState.PendingDelete or GoogleAlertState.Deleting => "已停止本地采集，等待核对远端删除结果。",
            _ => ""
        };
    }

    private async Task RefreshAsync(string? selection)
    {
        var data = await client.AskAsync(new("authors"), lifetime);
        SetAuthors(data.Authors ?? [], selection);
    }

    private Task OpenGoogleAlertsAsync() => WithFeedbackAsync(() =>
    {
        Process.Start(new ProcessStartInfo("https://www.google.com/alerts") { UseShellExecute = true });
        subscription.Text = "已打开 Google Alerts。请为当前作者创建提醒，并将投递方式设置为 RSS。";
        return Task.CompletedTask;
    }, lifetime);

    private Task CopySearchQueryAsync() => WithFeedbackAsync(() =>
    {
        var query = WatchInput.GoogleAlertQuery(account.Text);
        Clipboard.SetText(query);
        subscription.Text = $"已复制搜索条件：{query}。请在 Google Alerts 中创建提醒，再复制其 RSS 地址。";
        return Task.CompletedTask;
    }, lifetime);

    private Task ConnectGoogleAlertsAsync() => WithFeedbackAsync(async () =>
    {
        var selection = selected?.Account;
        subscription.Text = "请在打开的专用 Chrome 中登录。连接后将同步观察名单中的待处理提醒。";
        var result = await GoogleAlertsDesktopSync.RunAsync(client, lifetime, connect: true);
        await RefreshAsync(selection);
        subscription.Text = result ?? "";
    }, lifetime);

    private Task SyncGoogleAlertsAsync() => WithFeedbackAsync(async () =>
    {
        var selection = selected?.Account;
        var result = await GoogleAlertsDesktopSync.RunAsync(client, lifetime);
        await RefreshAsync(selection);
        subscription.Text = result ?? "没有待同步提醒，或尚未连接 Google 快讯。";
    }, lifetime);

    private Task DisconnectGoogleAlertsAsync() => WithFeedbackAsync(async () =>
    {
        await client.SaveAsync(new("alert-account-save", ConnectedGoogleAccount: ""), lifetime);
        subscription.Text = "已停止网页同步。现有 RSS 继续采集。";
    }, lifetime);

    private Task SaveAsync(bool archive) => WithFeedbackAsync(async () =>
    {
        var id = WatchInput.AccountId(account.Text);
        var draft = archive && selected is not null
            ? selected with { Archived = true, Active = false }
            : new Author(id, WatchInput.Text(name.Text, 40, true), note.Text, active.IsChecked == true,
                Revision: selected?.Revision ?? 0, FeedUrl: feedUrl.Text.Trim(),
                FullTextFeedUrl: fullTextFeed.Text.Trim());
        await client.SaveAsync(new("author-save", Author: draft), lifetime);
        string? result;
        try { result = await GoogleAlertsDesktopSync.RunAsync(client, lifetime, id); }
        finally { await RefreshAsync(id); }
        if (result is not null) subscription.Text = result;
        if (string.IsNullOrEmpty(subscription.Text))
            subscription.Text = archive ? "已移除作者，历史记录仍可查询。" : "作者已保存。";
    }, lifetime);

    private sealed record AuthorChoice(string Label, Author? Author);

}
