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
            AsyncAction("RSS 代理", OpenRssProxyAsync),
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
        feedUrl.IsReadOnly = author?.AlertState is GoogleAlertState.Creating or GoogleAlertState.Managed or
            GoogleAlertState.PendingDelete or GoogleAlertState.Deleting;
        active.IsChecked = author is null || author.Archived || author.Active;
        remove.IsEnabled = author is { Archived: false };
        subscription.Text = author?.AlertState switch
        {
            GoogleAlertState.PendingCreate => "等待创建提醒。Google 网页自动同步尚未接入，可先配置已有 RSS。",
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

    private Task OpenRssProxyAsync() => WithFeedbackAsync(async () =>
    {
        var settings = await client.AskAsync(new("rss-proxy"), lifetime);
        var address = new TextBox { Text = settings.RssProxy ?? "", MaxLength = 200 };
        new RssProxyEditor(this, address, client, lifetime).ShowDialog();
    }, lifetime);

    private Task SaveAsync(bool archive) => WithFeedbackAsync(async () =>
    {
        var id = WatchInput.AccountId(account.Text);
        var draft = archive && selected is not null
            ? selected with { Archived = true, Active = false }
            : new Author(id, WatchInput.Text(name.Text, 40, true), note.Text, active.IsChecked == true,
                Revision: selected?.Revision ?? 0, FeedUrl: feedUrl.Text.Trim());
        await client.SaveAsync(new("author-save", Author: draft), lifetime);
        await RefreshAsync(id);
        if (string.IsNullOrEmpty(subscription.Text))
            subscription.Text = archive ? "已移除作者，历史记录仍可查询。" : "作者已保存。";
    }, lifetime);

    private sealed record AuthorChoice(string Label, Author? Author);

    private sealed class RssProxyEditor : WatchEditor
    {
        internal RssProxyEditor(Window owner, TextBox address, WorkerWatchClient client, CancellationToken token)
            : base(owner, "Google Alerts RSS 代理", 520)
        {
            Field("Mihomo 本机 HTTP / Mixed 地址", address);
            Fields.Children.Add(new TextBlock
            {
                Text = "例如 http://127.0.0.1:7890。仅用于 Worker 读取 Google Alerts RSS，下次采集生效。清空地址可恢复默认网络。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new(0, 6, 0, 6)
            });
            Finish(
                AsyncAction("保存", () => WithFeedbackAsync(async () =>
                {
                    var proxy = WatchInput.NormalizeRssProxy(address.Text);
                    await client.SaveAsync(new("rss-proxy-save", RssProxy: proxy), token);
                    CloseAfterSave();
                }, token)),
                Action("取消", Close));
        }
    }
}
