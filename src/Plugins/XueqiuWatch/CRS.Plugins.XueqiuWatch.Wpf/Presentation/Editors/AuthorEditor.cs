using System.Windows;
using System.Windows.Controls;
using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class AuthorEditor : WatchEditor
{
    private const string SignInInstructions = "请在打开的普通 Chrome 窗口登录 Google。登录完成后关闭该窗口，再点击「同步 Google 快讯」。";
    private readonly WorkerWatchClient client;
    private readonly CancellationToken lifetime;
    private readonly ComboBox options = new() { DisplayMemberPath = nameof(AuthorChoice.Label) };
    private readonly TextBox account = new() { MaxLength = 200 };
    private readonly TextBox name = new() { MaxLength = 40 };
    private readonly TextBox note = new() { MaxLength = 80 };
    private readonly TextBox fullTextFeed = new() { MaxLength = 1024 };
    private readonly CheckBox active = new() { Content = "启用跟踪", IsChecked = true, Margin = new(0, 8, 0, 3) };
    private readonly TextBlock subscription = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 4) };
    private Author? selected;
    private readonly Button remove;
    private readonly Button save;

    public AuthorEditor(Window? owner, Author[] authors, WorkerWatchClient client, CancellationToken lifetime)
        : base(owner, "雪球观察名单", 650)
    {
        this.client = client;
        this.lifetime = lifetime;
        Field("作者", options);
        Field("雪球账号 ID 或主页链接", account);
        Field("显示名称", name);
        Field("备注", note);
        Field("获授权的全文 Feed 地址（选填）", fullTextFeed);
        fullTextFeed.ToolTip = "接受提供原帖链接、发布时间及完整纯文本的 JSON Feed 或 Atom。";
        Fields.Children.Add(active);
        subscription.SetResourceReference(TextBlock.ForegroundProperty, "BrandTextSecondaryBrush");
        Fields.Children.Add(subscription);
        remove = AsyncAction("移除", () => SaveAsync(true));
        save = AsyncAction("添加并同步", () => SaveAsync(false));
        Finish(
            AsyncAction("打开 Google 登录", OpenGoogleSignInAsync),
            AsyncAction("同步 Google 快讯", SyncGoogleAlertsAsync),
            AsyncAction("停止网页同步", DisconnectGoogleAlertsAsync),
            AsyncAction("刷新", () => WithFeedbackAsync(async () => await RefreshAsync(null), lifetime)),
            remove,
            save,
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
        fullTextFeed.Text = author?.FullTextFeedUrl ?? "";
        active.IsChecked = author is null || author.Archived || author.Active;
        remove.IsEnabled = author is { Archived: false };
        save.Content = author is null ? "添加并同步" : "保存";
        subscription.Text = author?.AlertState switch
        {
            null => "添加后将自动创建 Google Alerts RSS。首次使用时需在 Chrome 中登录 Google。",
            GoogleAlertState.PendingCreate => "作者已保存，等待创建 RSS。可点击「同步 Google 快讯」重试。",
            GoogleAlertState.Creating => "作者已保存，等待核对 Google Alerts 创建结果。",
            GoogleAlertState.Managed => "Google Alerts RSS 已创建并保存。",
            GoogleAlertState.External => "已绑定现有 RSS。移除作者只停止本地采集。",
            GoogleAlertState.PendingDelete or GoogleAlertState.Deleting => "已停止本地采集，等待核对远端删除结果。",
            _ => author is { Archived: true } ? "已移除作者，历史记录仍可查询。" :
                author is { Active: false } ? "已暂停跟踪。" : "尚未绑定 RSS。"
        };
    }

    private async Task RefreshAsync(string? selection)
    {
        var data = await client.AskAsync(new("authors"), lifetime);
        SetAuthors(data.Authors ?? [], selection);
    }

    private Task OpenGoogleSignInAsync() => WithFeedbackAsync(() =>
    {
        GoogleAlertsBrowserLogin.Open();
        subscription.Text = SignInInstructions;
        return Task.CompletedTask;
    }, lifetime);

    private Task SyncGoogleAlertsAsync() => WithFeedbackAsync(async () =>
    {
        var selection = selected?.Account;
        subscription.Text = "正在检查 Google 登录状态并同步 RSS…";
        string? result;
        try { result = await GoogleAlertsDesktopSync.RunAsync(client, lifetime, connect: true); }
        finally { await RefreshAsync(selection); }
        AppendSyncSummary(result);
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
                Revision: selected?.Revision ?? 0, FeedUrl: selected?.FeedUrl ?? "",
                FullTextFeedUrl: fullTextFeed.Text.Trim());
        await client.SaveAsync(new("author-save", Author: draft), lifetime);
        subscription.Text = archive ? "已停止本地采集，正在核对 Google 快讯…" :
            "作者已保存，正在检查 Google 登录状态并同步 RSS…";
        string? result = null;
        var needsSignIn = false;
        try { result = await GoogleAlertsDesktopSync.RunAsync(client, lifetime, id, connectIfNeeded: !archive); }
        catch (GoogleAlertsSignInRequiredException) when (!archive) { needsSignIn = true; }
        finally { await RefreshAsync(id); }
        if (needsSignIn)
        {
            GoogleAlertsBrowserLogin.Open();
            AppendSyncSummary(SignInInstructions);
            return;
        }
        if (selected?.AlertState is GoogleAlertState.PendingCreate or GoogleAlertState.Creating or
            GoogleAlertState.PendingDelete or GoogleAlertState.Deleting)
            AppendSyncSummary(result);
    }, lifetime);

    private void AppendSyncSummary(string? summary)
    {
        if (!string.IsNullOrEmpty(summary))
            subscription.Text = $"{subscription.Text}\n{summary}";
    }

    private sealed record AuthorChoice(string Label, Author? Author);

}
