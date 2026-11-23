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
        Field("Google Alerts RSS 地址（每位作者独立配置）", feedUrl);
        Fields.Children.Add(new TextBlock
        {
            Text = "低成本订阅方式：点击「打开 Google Alerts」，按下方生成的搜索条件创建一个提醒；建议来源保持自动、语言选择中文、结果数量选择「所有结果」，投递方式选择 RSS，再把 Google 生成的 RSS 地址粘贴到上方。每位作者单独创建一个提醒。Google Alerts 免费，但搜索收录和更新时效不保证完整。",
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
        active.IsChecked = author is null || author.Archived || author.Active;
        remove.IsEnabled = author is { Archived: false };
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

    private Task SaveAsync(bool archive) => WithFeedbackAsync(async () =>
    {
        var id = WatchInput.AccountId(account.Text);
        var draft = archive && selected is not null
            ? selected with { Archived = true, Active = false }
            : new Author(id, WatchInput.Text(name.Text, 40, true), note.Text, active.IsChecked == true,
                Revision: selected?.Revision ?? 0, FeedUrl: feedUrl.Text.Trim());
        await client.SaveAsync(new("author-save", Author: draft), lifetime);
        await RefreshAsync(archive ? null : id);
        subscription.Text = string.IsNullOrWhiteSpace(draft.FeedUrl)
            ? "作者已保存。当前未配置 Google Alerts RSS，后台不会采集该作者。"
            : "作者及 Google Alerts RSS 地址已保存。Worker 将按作者独立采集；搜索引擎未收录的文章不会出现在订阅中。";
    }, lifetime);

    private sealed record AuthorChoice(string Label, Author? Author);
}
