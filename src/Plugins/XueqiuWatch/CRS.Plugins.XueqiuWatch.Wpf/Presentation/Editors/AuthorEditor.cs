using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CRS.Plugins.XueqiuWatch.Core;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class AuthorEditor : WatchEditor
{
    private readonly WorkerWatchClient client;
    private readonly CancellationToken lifetime;
    private readonly DataGrid list = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
        CanUserReorderColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, RowHeight = 38
    };
    private readonly TextBlock empty = new()
    {
        Text = "暂无作者，使用右上角的添加图标开始跟踪。",
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };
    private readonly TextBlock count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { Margin = new(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button edit;
    private readonly MenuItem remove;

    private Author? SelectedAuthor => (list.SelectedItem as AuthorRow)?.Author;

    public AuthorEditor(Window? owner, Author[] authors, WorkerWatchClient client, CancellationToken lifetime)
        : base(owner, "雪球观察名单", 720)
    {
        this.client = client;
        this.lifetime = lifetime;
        SizeToContent = SizeToContent.Manual;
        Height = 510;
        MinWidth = 570;
        MinHeight = 380;

        var add = IconButton(AsyncAction("添加作者", () => EditAuthorAsync(null)), SymbolRegular.PersonAdd20);
        add.SetResourceReference(StyleProperty, "UiPrimaryButton");
        edit = IconButton(AsyncAction("编辑所选作者", () => EditAuthorAsync(SelectedAuthor)), SymbolRegular.Edit20);
        remove = MenuAction("移除所选作者", SymbolRegular.PersonDelete20, RemoveAuthorAsync);
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuAction("刷新名单", SymbolRegular.ArrowClockwise20,
            () => WithFeedbackAsync(() => RefreshAsync(SelectedAuthor?.Account), lifetime)));
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        var more = IconButton(Action("更多操作", () => menu.IsOpen = true), SymbolRegular.MoreHorizontal20);
        more.ContextMenu = menu;
        menu.PlacementTarget = more;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(add);
        actions.Children.Add(edit);
        actions.Children.Add(more);
        add.Margin = edit.Margin = new(0, 0, 6, 0);
        DockPanel.SetDock(actions, Dock.Right);
        var toolbar = new DockPanel { Margin = new(0, 0, 0, 12) };
        toolbar.Children.Add(actions);
        toolbar.Children.Add(count);

        list.SetResourceReference(StyleProperty, "UiDenseDataGrid");
        list.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "UiPluginTableHeader");
        list.SetResourceReference(DataGrid.RowStyleProperty, "UiPluginTableRow");
        list.SetResourceReference(DataGrid.CellStyleProperty, "UiPluginTableCell");
        AutomationProperties.SetName(list, "观察名单作者");
        var cellText = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
        cellText.Setters.Add(new Setter(MarginProperty, new Thickness(10, 0, 8, 0)));
        cellText.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        cellText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        foreach (var (header, property, width) in new (string, string, DataGridLength)[]
        {
            ("作者", nameof(AuthorRow.Name), new(1, DataGridLengthUnitType.Star)),
            ("雪球账号 ID", nameof(AuthorRow.Account), new(146)),
            ("跟踪状态", nameof(AuthorRow.TrackingStatus), new(105))
        })
        {
            list.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new Binding(property), Width = width,
                ElementStyle = cellText
            });
        }
        list.SelectionChanged += (_, _) => UpdateSelectionActions();
        list.MouseDoubleClick += async (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(list, source) is DataGridRow { Item: AuthorRow row })
            {
                e.Handled = true;
                await EditAuthorAsync(row.Author);
            }
        };
        var table = new Grid();
        table.Children.Add(list);
        table.Children.Add(empty);
        empty.SetResourceReference(StyleProperty, "UiHintText");

        count.SetResourceReference(StyleProperty, "UiHintText");
        status.SetResourceReference(StyleProperty, "UiStatusText");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var close = IconButton(Action("关闭", Close), SymbolRegular.Dismiss20);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new(0, 10, 0, 0);
        var footer = new StackPanel();
        footer.Children.Add(status);
        footer.Children.Add(ErrorText);
        footer.Children.Add(close);
        ErrorText.Margin = new(0);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);

        var layout = new DockPanel { Margin = new(18) };
        layout.Children.Add(toolbar);
        layout.Children.Add(footer);
        layout.Children.Add(table);
        Content = layout;
        SetAuthors(authors, null);
    }

    private void SetAuthors(Author[] authors, string? selection)
    {
        var rows = authors.Select(author => new AuthorRow(author)).ToArray();
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(row => row.Account == selection);
        if (list.SelectedItem is { } selected) list.ScrollIntoView(selected);
        empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        count.Text = $"共 {rows.Length} 位作者";
        UpdateSelectionActions();
    }

    private void UpdateSelectionActions()
    {
        edit.IsEnabled = SelectedAuthor is not null;
        remove.IsEnabled = SelectedAuthor is { Archived: false };
    }

    private async Task RefreshAsync(string? selection)
    {
        var data = await client.AskAsync(new("authors"), lifetime);
        SetAuthors(data.Authors ?? [], selection);
    }

    private async Task EditAuthorAsync(Author? author)
    {
        var dialog = new AuthorDetailsEditor(this, author, client, lifetime);
        if (dialog.ShowDialog() == true)
            await WithFeedbackAsync(async () =>
            {
                await RefreshAsync(dialog.SavedAccount);
                ShowStatus("作者已保存。");
                if (dialog.SavedActive && author?.Active != true)
                    ShowStatus(await client.StartBackfillAsync(dialog.SavedAccount, lifetime));
            }, lifetime);
    }

    private Task RemoveAuthorAsync() => WithFeedbackAsync(async () =>
    {
        if (SelectedAuthor is not { Archived: false } author) return;
        await client.SaveAsync(new("author-save", Author: author with { Archived = true, Active = false }), lifetime);
        await RefreshAsync(author.Account);
        ShowStatus("已停止跟踪该作者。");
    }, lifetime);

    private void ShowStatus(string? message)
    {
        status.Text = message;
        status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Button IconButton(Button button, SymbolRegular symbol)
    {
        var caption = (string)button.Content;
        button.Content = new SymbolIcon { Symbol = symbol, FontSize = 18 };
        button.ToolTip = caption;
        AutomationProperties.SetName(button, caption);
        ToolTipService.SetShowOnDisabled(button, true);
        button.SetResourceReference(StyleProperty, "UiPluginAction");
        button.Width = button.Height = 36;
        button.MinWidth = button.MinHeight = 0;
        button.Padding = new(0);
        button.Margin = new(0);
        return button;
    }

    private static MenuItem MenuAction(string caption, SymbolRegular symbol, Func<Task> handler)
    {
        var item = new MenuItem { Header = caption, Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 } };
        item.Click += async (_, _) => await handler();
        return item;
    }

    private sealed record AuthorRow(Author Author)
    {
        public string Name => Author.Name;
        public string Account => Author.Account;
        public string TrackingStatus => Author.Archived ? "已移除" : Author.Active ? "跟踪中" : "已暂停";
    }
}
