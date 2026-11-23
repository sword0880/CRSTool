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
        : base(owner, "雪球观察名单", 620)
    {
        this.client = client;
        this.lifetime = lifetime;
        Field("作者", options);
        Field("雪球账号 ID 或主页链接", account);
        Field("显示名称", name);
        Field("备注", note);
        Field("RSS 地址（每位作者独立配置）", feedUrl);
        Fields.Children.Add(new TextBlock
        {
            Text = "可粘贴有权使用的 RSS / Atom / JSON Feed 地址。RSSHub 示例路由为 https://rsshub.app/xueqiu/user/{账号ID}；公共实例属于第三方服务，可替换为自建 RSSHub 或其他来源。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 6)
        });
        Fields.Children.Add(active);
        Fields.Children.Add(subscription);
        remove = AsyncAction("移除", () => SaveAsync(true));
        Finish(
            AsyncAction("填入 RSSHub 示例", FillRssHubExampleAsync),
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

    private Task FillRssHubExampleAsync() => WithFeedbackAsync(() =>
    {
        var id = WatchInput.AccountId(account.Text);
        feedUrl.Text = $"https://rsshub.app/xueqiu/user/{id}";
        subscription.Text = "已填入公共 RSSHub 示例地址。请确认来源可用及内容使用权限；生产环境优先使用自建实例或授权来源。";
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
            ? "作者已保存。当前未配置 RSS，后台不会采集该作者。"
            : "作者及 RSS 地址已保存。Worker 将按作者独立采集；来源返回空列表不代表完整覆盖。";
    }, lifetime);

    private sealed record AuthorChoice(string Label, Author? Author);
}
