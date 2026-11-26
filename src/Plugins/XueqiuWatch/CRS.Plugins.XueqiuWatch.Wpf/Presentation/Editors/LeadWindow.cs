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
    internal Opinion? SavedOpinion { get; private set; }

    internal LeadWindow(Window? owner, WorkerWatchClient client, CancellationToken lifetime)
        : base(owner, "雪球发言线索", 890)
    {
        this.client = client;
        this.lifetime = lifetime;
        Fields.Children.Add(source);
        Fields.Children.Add(AsyncAction("观察名单", ConfigureAsync));

        SetColumns(coverage, ("作者", nameof(AuthorCoverage.Author)),
            ("最近检查（北京时间）", nameof(AuthorCoverage.Checked)), ("采集状态", nameof(AuthorCoverage.Status)));
        Field("作者状态", coverage);
        Field("筛选作者", filter);
        filter.SelectionChanged += async (_, _) => await BrowseAsync(0);

        SetColumns(links, ("作者", nameof(LeadRow.Author)), ("原帖发布时间（北京时间）", nameof(LeadRow.Published)),
            ("文章标题", nameof(LeadRow.Title)), ("正文状态与片段", nameof(LeadRow.RssContent)),
            ("解析线索（规则候选）", nameof(LeadRow.Hints)), ("雪球原链接（待核验）", nameof(LeadRow.Url)));
        Field("发言线索", links);
        var pagination = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 6, 0, 0) };
        pagination.Children.Add(page);
        prior = AsyncAction("上一页", () => BrowseAsync(Math.Max(0, offset - WatchProtocol.LeadsPerPage)));
        next = AsyncAction("下一页", () => BrowseAsync(offset + WatchProtocol.LeadsPerPage));
        pagination.Children.Add(prior);
        pagination.Children.Add(next);
        Fields.Children.Add(pagination);

        Finish(AsyncAction("刷新", () => BrowseAsync(offset)), AsyncAction("打开选中链接", OpenLeadAsync),
            AsyncAction("查看正文", OpenPostBodyAsync), AsyncAction("从选中线索录入观点", RecordOpinionAsync),
            Action("关闭", Close));
        Loaded += async (_, _) => await BrowseAsync(0);
    }

    private Task ConfigureAsync() => WithFeedbackAsync(async () =>
    {
        var data = await client.AskAsync(new("authors"), lifetime);
        new AuthorEditor(this, data.Authors ?? [], client, lifetime).ShowDialog();
        await BrowseCoreAsync(0);
    }, lifetime);

    private Task BrowseAsync(int requestedOffset) =>
        WithFeedbackAsync(() => BrowseCoreAsync(requestedOffset), lifetime);

    private async Task BrowseCoreAsync(int requestedOffset)
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

        source.Text = result.TimelineSupported
            ? "在任务中心设置「发现雪球发言线索」的执行时间。"
            : "当前 Worker 使用 RSS 采集。可在观察名单的「更多 → 备用 RSS」中配置。";

        authors = result.Authors;
        var byAccount = authors.ToDictionary(item => item.Account);
        var options = new[] { new Author("", "全部作者") }.Concat(result.Authors).ToArray();
        filter.ItemsSource = options;
        filter.SelectedItem = options.FirstOrDefault(item => item.Account == account) ?? options[0];
        coverage.ItemsSource = result.Coverage.Select(item =>
        {
            var author = byAccount[item.Account];
            var checkedAt = item.Timeline?.CheckedAt ?? item.CheckedAt;
            return new AuthorCoverage(author.Name, checkedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—",
                CoverageText(author, item, result.TimelineSupported));
        }).ToArray();
        Security[] securities = result.Rows.Length == 0
            ? []
            : (await client.AskAsync(new("catalog"), lifetime)).Securities ?? [];
        links.ItemsSource = result.Rows.Select(item =>
        {
            var articleText = $"{item.Title}\n{item.Summary}\n{item.Content}";
            var stocks = WatchInput.Candidates(articleText, securities);
            var directions = WatchInput.DirectionPhrases(articleText);
            var hints = new List<string>();
            if (stocks.Count > 0)
                hints.Add("股票候选：" + string.Join("、", stocks.Select(stock => $"{stock.Name}（{stock.Market} {stock.Code}）")));
            if (directions.Length > 0)
                hints.Add("多空措辞：" + string.Join("、", directions));
            return new LeadRow(byAccount[item.Account].Name, item,
                hints.Count == 0 ? "暂未识别（需查看原文）" : string.Join("；", hints));
        }).ToArray();
        page.Text = result.Total == 0 ? "暂无已保存线索" : $"第 {offset + 1}–{offset + result.Rows.Length} 条，共 {result.Total} 条";
        prior.IsEnabled = offset > 0;
        next.IsEnabled = offset + result.Rows.Length < result.Total;
    }

    private static string CoverageText(Author author, FeedCoverage item, bool timelineSupported)
    {
        if (!author.Active || author.Archived) return "已暂停";
        var rss = item.State switch
        {
            "Read" when item.Matches is > 0 => $"RSS 返回 {item.Matches} 条",
            "Read" => "RSS 无匹配线索",
            "NotModified" => "RSS 未变化",
            "RateLimited" => "RSS 限流",
            "AccessRequired" => "RSS 访问受限",
            "TimedOut" => "RSS 读取超时",
            "ProxyUnavailable" => "RSS 代理不可用",
            "NotConfigured" or "NeverChecked" => "",
            _ => "RSS 读取失败"
        };
        var discovery = item.Timeline switch
        {
            { State: "Read", Matches: > 0 } timeline => $"API 返回 {timeline.Matches} 条",
            { State: "Read" } => "API 无新增动态",
            { State: "Backfilling" } => "正在回补近 30 天",
            { State: "Partial" } => "API 动态未读全",
            { State: "RateLimited" } => "API 限流",
            { State: "AccessRestricted" } => "API 访问受限",
            not null => "API 读取失败",
            _ => "尚未检查"
        };
        if (!timelineSupported) discovery = rss.Length == 0 ? "尚未检查" : rss;
        else if (item.Timeline?.State is not ("Read" or "Backfilling") && rss.Length > 0) discovery += $" · 备用 {rss}";
        if (item.BackfillPending && item.Timeline?.State != "Backfilling") discovery += " · 近 30 天回补待续";
        var fullText = item.FullTextState switch
        {
            null => "",
            "Read" => $" · 自动补全 {item.FullTextCount ?? 0} 条",
            "NeverChecked" => " · 全文源尚未检查",
            "RateLimited" => " · 全文源限流",
            "NotModified" => " · 全文源未变化",
            _ => " · 全文源读取失败"
        };
        return discovery + fullText;
    }

    private Task OpenLeadAsync() => WithFeedbackAsync(() =>
    {
        if (links.SelectedItem is not LeadRow row) throw new ArgumentException("请先选择一条线索。");
        try { Process.Start(new ProcessStartInfo(WatchInput.SourceLink(row.Url)) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { throw new ArgumentException("无法打开原帖链接。"); }
        return Task.CompletedTask;
    }, lifetime);

    private Task RecordOpinionAsync() => WithFeedbackAsync(async () =>
    {
        if (links.SelectedItem is not LeadRow row) throw new ArgumentException("请先选择一条线索。");
        var directory = (await client.AskAsync(new("catalog"), lifetime)).Securities ?? [];
        var document = await client.ReadPostBodyAsync(new(row.Lead.Account, row.Url), lifetime);
        var editor = new OpinionEditor(this, authors, directory, null, client, lifetime, row.Lead, document);
        if (editor.ShowDialog() != true) return;
        SavedOpinion = editor.SavedOpinion;
        CloseAfterSave();
    }, lifetime);

    private Task OpenPostBodyAsync() => WithFeedbackAsync(async () =>
    {
        if (links.SelectedItem is not LeadRow row) throw new ArgumentException("请先选择一条线索。");
        await PostBodyViewer.OpenAsync(this, client, new(row.Lead.Account, row.Url),
            WatchInput.PostTime(row.Lead.PublishedAt, WatchProtocol.BeijingNow), lifetime);
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
    private sealed record LeadRow(string Author, PostLead Lead, string Hints)
    {
        public string Published => WatchInput.PostTime(Lead.PublishedAt, WatchProtocol.BeijingNow) is { } time
            ? (Lead.PostBodyRevision > 0 || Lead.OriginalPageTimeVerified ? "" : "RSS · ") +
                time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm")
            : "待核对";
        public string Title => WatchInput.DisplayStockTags(Lead.Title);
        public string RssContent => Lead.PostBodyRevision > 0 ? $"全文 · {PostBody.SourceLabel(Lead.BodySource)}" :
            OriginalPageStatus + (Lead.Summary.Length > 0 ? $"片段 · {WatchInput.DisplayStockTags(Lead.Summary)}" :
                Lead.Content.Length > 0 ? $"片段 · {WatchInput.DisplayStockTags(Lead.Content)}" : "仅链接");
        private string OriginalPageStatus
        {
            get
            {
                var status = Lead.OriginalPageState switch
                {
                    "BodyUnavailable" => "原帖正文不完整",
                    "AccessRestricted" => "原帖访问受限",
                    "RateLimited" => "雪球限流",
                    "Unavailable" => "原帖读取失败",
                    _ => ""
                };
                if (status.Length == 0) return "";
                return Lead.OriginalPageHttpStatus is { } code ? $"{status}（HTTP {code}） · " : $"{status} · ";
            }
        }
        public string Url => Lead.Url;
    }
}
