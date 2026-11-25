using System.Windows;
using System.Windows.Controls;
using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class AuthorDetailsEditor : WatchEditor
{
    private readonly TextBox account = new() { MaxLength = 200 };
    private readonly TextBox name = new() { MaxLength = 40 };
    private readonly TextBox note = new() { MaxLength = 80 };
    private readonly TextBox fullTextFeed = new() { MaxLength = 1024 };
    private readonly CheckBox active = new() { Content = "启用跟踪", Margin = new(0, 10, 0, 4) };

    public string SavedAccount { get; private set; } = "";

    public AuthorDetailsEditor(Window owner, Author? author, WorkerWatchClient client, CancellationToken lifetime)
        : base(owner, author is null ? "添加雪球作者" : "编辑雪球作者", 520)
    {
        ResizeMode = ResizeMode.NoResize;
        account.IsReadOnly = author is not null;
        account.Text = author?.Account ?? "";
        name.Text = author?.Name ?? "";
        note.Text = author?.Note ?? "";
        fullTextFeed.Text = author?.FullTextFeedUrl ?? "";
        active.IsChecked = author is null || author.Archived || author.Active;

        Field(author is null ? "雪球账号 ID 或主页链接" : "雪球账号 ID", account);
        Field("显示名称", name);
        Field("备注（选填）", note);
        Field("获授权的全文 Feed 地址（选填）", fullTextFeed);
        fullTextFeed.ToolTip = "接受提供原帖链接、发布时间及完整纯文本的 JSON Feed 或 Atom。";
        Fields.Children.Add(active);
        if (author is null || author.Archived)
        {
            var hint = new TextBlock
            {
                Text = author is null ? "启用跟踪后会尝试创建 Google 快讯 RSS。首次使用时需要登录 Google。" :
                    "保存后会将这位作者重新加入观察名单。",
                Margin = new(0, 4, 0, 0)
            };
            hint.SetResourceReference(StyleProperty, "UiHintText");
            Fields.Children.Add(hint);
        }

        var save = AsyncAction(author is null ? "添加并同步" : "保存", () => WithFeedbackAsync(async () =>
        {
            var id = WatchInput.AccountId(account.Text);
            var draft = new Author(id, WatchInput.Text(name.Text, 40, true), note.Text, active.IsChecked == true,
                Revision: author?.Revision ?? 0, FeedUrl: author?.FeedUrl ?? "", FullTextFeedUrl: fullTextFeed.Text.Trim());
            await client.SaveAsync(new("author-save", Author: draft), lifetime);
            SavedAccount = id;
            CloseAfterSave();
        }, lifetime));
        save.SetResourceReference(StyleProperty, "UiPrimaryButton");
        Finish(new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 8, 5), Padding = new(9, 5, 9, 5) }, save);
        Loaded += (_, _) => (author is null ? account : name).Focus();
    }
}
