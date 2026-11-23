using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class LeadWindow : WatchEditor
{
    private readonly WorkerWatchClient client;
    private readonly CancellationToken lifetime;
    private readonly TextBlock source = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8) };
    private readonly TextBlock page = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
    private readonly ComboBox filter = new() { DisplayMemberPath = nameof(Author.Name) };
    private readonly DataGrid coverage = Grid(195);
    private readonly DataGrid links = Grid(205);
    private readonly Button prior;
    private readonly Button next;
    private Author[] authors = [];
    private int offset;

    internal LeadWindow(Window? owner, WorkerWatchClient client, CancellationToken lifetime)
        : base(owner, "雪球发言线索", 890)
    {
        this.client = client;
        this.lifetime = lifetime;
        Fields.Children.Add(source);
        Fields.Children.Add(Action("连接 Google", Configure));
        Fields.Children.Add(new TextBlock
        {
            Text = "到「任务中心 → 服务计划 → 添加计划」设置“发现雪球发言线索”的执行时间。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 10)
        });

        SetColumns(coverage, ("作者", nameof(AuthorCoverage.Author)),
            ("最近检查（北京时间）", nameof(AuthorCoverage.Checked)), ("结果与覆盖", nameof(AuthorCoverage.Status)));
        Field("作者检查状态", coverage);
        Field("筛选作者", filter);
        filter.SelectionChanged += async (_, _) => await BrowseAsync(0);

        SetColumns(links, ("作者", nameof(LeadRow.Author)), ("首次发现（北京时间）", nameof(LeadRow.Found)),
            ("雪球原链接（待核验）", nameof(LeadRow.Url)));
        Field("发言线索", links);
        var pagination = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 6, 0, 0) };
        pagination.Children.Add(page);
        prior = AsyncAction("上一页", () => BrowseAsync(Math.Max(0, offset - WatchProtocol.LeadsPerPage)));
        next = AsyncAction("下一页", () => BrowseAsync(offset + WatchProtocol.LeadsPerPage));
        pagination.Children.Add(prior);
        pagination.Children.Add(next);
        Fields.Children.Add(pagination);

        Finish(AsyncAction("刷新", () => BrowseAsync(offset)), AsyncAction("打开选中链接", OpenLeadAsync),
            Action("关闭", Close));
        Loaded += async (_, _) => await BrowseAsync(0);
    }

    private async void Configure()
    {
        new SubscriptionEditor(this, client, lifetime).ShowDialog();
        await BrowseAsync(0);
    }

    private Task BrowseAsync(int requestedOffset) => WithFeedbackAsync(async () =>
    {
        var account = (filter.SelectedItem as Author)?.Account;
        var current = await client.AskAsync(new("leads", new(Account: account, Offset: requestedOffset)), lifetime);
        var result = current.Leads ?? throw new InvalidDataException("缺少发言线索。");
        offset = requestedOffset;
        if (result.Rows.Length == 0 && offset != 0)
        {
            offset = 0;
            result = (await client.AskAsync(new("leads", new(Account: account)), lifetime)).Leads
                ?? throw new InvalidDataException("缺少发言线索。");
        }
        source.Text = result.SubscriptionPending
            ? "观察名单的 RSS 订阅待同步。可在「连接 Google」中查看或重试。"
            : result.Configured
            ? "已配置订阅。这里显示索引线索，作者的完整发言范围仍待核验。"
            : "当前没有 RSS 订阅。已有线索可继续查看。";
        authors = result.Authors;
        var byAccount = authors.ToDictionary(item => item.Account);
        var options = new[] { new Author("", "全部作者") }.Concat(result.Authors).ToArray();
        filter.ItemsSource = options;
        filter.SelectedItem = options.FirstOrDefault(item => item.Account == account) ?? options[0];
        coverage.ItemsSource = result.Coverage.Select(item =>
        {
            var author = byAccount[item.Account];
            return new AuthorCoverage(author.Name, item.CheckedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—",
                CoverageText(author, item));
        }).ToArray();
        links.ItemsSource = result.Rows.Select(item => new LeadRow(byAccount[item.Account].Name,
            item.FirstSeen.ToString("yyyy-MM-dd HH:mm"), item.Url)).ToArray();
        page.Text = result.Total == 0 ? "暂无已保存线索" : $"第 {offset + 1}–{offset + result.Rows.Length} 条，共 {result.Total} 条";
        prior.IsEnabled = offset > 0;
        next.IsEnabled = offset + result.Rows.Length < result.Total;
    }, lifetime);

    private static string CoverageText(Author author, FeedCoverage item)
    {
        if (!author.Active || author.Archived) return "已暂停";
        return item.State switch
        {
            "Read" when item.Matches > 0 => $"订阅命中 {item.Matches} 条 · 待核验",
            "Read" => "订阅未命中 · 发言覆盖未知",
            "RateLimited" => "来源限流 · 待补查",
            "AccessRequired" => "订阅访问受限",
            "InvalidFeed" => "订阅格式异常",
            "TimedOut" => "订阅读取超时",
            "FeedUnavailable" => "订阅暂不可用",
            _ => "尚未检查"
        };
    }

    private Task OpenLeadAsync() => WithFeedbackAsync(() =>
    {
        if (links.SelectedItem is not LeadRow row) throw new ArgumentException("请先选择一条线索。");
        try { Process.Start(new ProcessStartInfo(WatchInput.SourceLink(row.Url)) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { throw new ArgumentException("无法打开原帖链接。"); }
        return Task.CompletedTask;
    }, lifetime);

    private static DataGrid Grid(int height)
    {
        var table = new DataGrid
        {
            Height = height, IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single
        };
        table.SetResourceReference(FrameworkElement.StyleProperty, "UiDenseDataGrid");
        return table;
    }

    private static void SetColumns(DataGrid grid, params (string Header, string Property)[] columns)
    {
        foreach (var (header, property) in columns)
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = new(1, DataGridLengthUnitType.Star) });
    }

    private sealed record AuthorCoverage(string Author, string Checked, string Status);
    private sealed record LeadRow(string Author, string Found, string Url);
}
