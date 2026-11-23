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
    private readonly CheckBox active = new() { Content = "启用跟踪", IsChecked = true, Margin = new(0, 8, 0, 3) };
    private readonly TextBlock subscription = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 4) };
    private Author? selected;
    private readonly Button remove;

    public AuthorEditor(Window? owner, Author[] authors, WorkerWatchClient client, CancellationToken lifetime)
        : base(owner, "雪球观察名单", 500)
    {
        this.client = client;
        this.lifetime = lifetime;
        Field("作者", options);
        Field("雪球账号 ID 或主页链接", account);
        Field("显示名称", name);
        Field("备注", note);
        Fields.Children.Add(active);
        Fields.Children.Add(subscription);
        remove = AsyncAction("移除", () => SaveAsync(true));
        Finish(Action("连接 Google", ShowSubscription),
            AsyncAction("刷新", () => WithFeedbackAsync(async () => await RefreshAsync(null), lifetime)),
            remove, AsyncAction("保存", () => SaveAsync(false)), Action("关闭", Close));
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
        active.IsChecked = author is null || author.Archived || author.Active;
        remove.IsEnabled = author is { Archived: false };
    }

    private async Task RefreshAsync(string? skey)
    {
        var data = await client.AskAsync(new("authors"), lifetime);
        SetAuthors(data.Authors ?? [], skey);
    }

    private Task SaveAsync(bool archive) => WithFeedbackAsync(async () =>
    {
        var id = WatchInput.AccountId(account.Text);
        var draft = archive && selected is not null
            ? selected with { Archived = true, Active = false }
            : new Author(id, WatchInput.Text(name.Text, 40, true), note.Text, active.IsChecked == true,
                Revision: selected?.Revision ?? 0);
        await client.SaveAsync(new("author-save", Author: draft), lifetime);
        await RefreshAsync(archive ? null : id);
        subscription.Text = "作者已保存，正在核对 Google RSS 订阅…";
        subscription.Text = "作者已保存。" + await new GoogleAlertsSync(client).SyncAsync(lifetime);
    }, lifetime);

    private void ShowSubscription()
    {
        var editor = new SubscriptionEditor(this, client, lifetime);
        editor.ShowDialog();
        if (editor.SyncStatus is { } result) subscription.Text = result;
    }

    private sealed record AuthorChoice(string Label, Author? Author);
}
