using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Ipc;

namespace CRS.Plugins.XueqiuWatch.Wpf;

public partial class WatchPage : UserControl
{
    private const double ColumnWidth = 124;
    private static readonly double[] RowHeights = [29, 45, 31, 31];
    private readonly WorkerWatchClient client;
    private readonly CancellationToken lifetime;
    private Author[] authors = [];
    private WatchOverview? overview;
    private StockSummary? selected;
    private bool loaded;
    private bool updating;
    private int stockOffset;
    private int detailOffset;
    private string mode = "latest";

    internal WatchPage(WorkerWatchClient client, CancellationToken lifetime)
    {
        this.client = client;
        this.lifetime = lifetime;
        InitializeComponent();
        Details.RowHeight = double.NaN;
        PeriodFilter.ItemsSource = new[] { "近 30 天", "近 7 天" };
        PeriodFilter.SelectedIndex = 0;
        MarketFilter.ItemsSource = new[] { "全部市场" }.Concat(WatchProtocol.Markets);
        MarketFilter.SelectedIndex = 0;
        IndustryFilter.ItemsSource = new[] { "全部行业" };
        IndustryFilter.SelectedIndex = 0;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        await RunAsync(() => RefreshAsync());
    }

    private async void ShowLeads(object sender, RoutedEventArgs e)
    {
        var dialog = new LeadWindow(Window.GetWindow(this), client, lifetime);
        dialog.ShowDialog();
        await RunAsync(async () =>
        {
            if (dialog.SavedOpinion is { } saved)
                await RefreshSavedOpinionAsync(saved);
            await RefreshLeadCountAsync();
        });
    }

    private async Task RefreshSavedOpinionAsync(Opinion saved)
    {
        await RefreshStocksAsync(saved);
        var message = saved.Security is null || selected?.Security.Key == saved.Security.Key
            ? "线索已录入为观点。" : "线索已录入为观点。当前股票页未显示对应股票，请核对筛选范围。";
        Status.Text = $"{message} {Status.Text}";
    }

    private ReviewFilter Filters() => new(PeriodFilter.SelectedIndex == 1 ? 7 : 30,
        (AuthorFilter.SelectedItem as Author)?.Account is { Length: > 0 } account ? account : null,
        MarketFilter.SelectedIndex > 0 ? MarketFilter.SelectedItem as string : null,
        IndustryFilter.SelectedIndex > 0 ? IndustryFilter.SelectedItem as string : null, StockSearch.Text);

    private async Task RunAsync(Func<Task> action)
    {
        if (updating || lifetime.IsCancellationRequested) return;
        updating = true;
        IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or IOException or JsonException or InvalidOperationException or BackgroundServiceException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status.Text = error switch
            {
                WatchAccessException { Code: "Forbidden" } => "此页面保存私人研究资料，请以 Windows 管理员权限打开 CRS。",
                WatchAccessException { Code: "UnknownCommand" or "UnknownQuery" } =>
                    "配套 Worker 尚未加载雪球观点追踪插件。请使用包含该插件的 Worker 版本。",
                WatchAccessException { Code: "PluginBusy" } => "后台正在处理观点资料，请稍后刷新。",
                WatchAccessException service => $"后台未能读取或保存观点资料（{service.Code}）。",
                BackgroundServiceException transport => transport.Message,
                ArgumentException invalid => invalid.Message,
                _ => "观点资料操作失败，请检查文件、链接或后台状态。"
            };
        }
        finally { IsEnabled = true; updating = false; }
    }

    private async Task RefreshAsync()
    {
        var account = (AuthorFilter.SelectedItem as Author)?.Account;
        authors = (await client.AskAsync(new("authors"), lifetime)).Authors ?? [];
        var authorOptions = new[] { new Author("", "全部作者") }.Concat(authors).ToArray();
        AuthorFilter.ItemsSource = authorOptions;
        AuthorFilter.SelectedItem = authorOptions.FirstOrDefault(x => x.Account == account) ?? authorOptions[0];
        await RefreshStocksAsync();
        await RefreshLeadCountAsync();
    }

    private async Task RefreshLeadCountAsync()
    {
        try
        {
            var leads = (await client.AskAsync(new("leads"), lifetime)).Leads;
            LeadsButton.Content = leads is { Total: > 0 } ? $"发言线索（{leads.Total}）" : "发言线索";
        }
        catch (ArgumentException error) when (error.Message == "未知的观点追踪操作。")
        {
            // 旧版 Worker 未提供线索查询时，股票观点仍可刷新。
            LeadsButton.Content = "发言线索";
        }
    }

    private async Task RefreshStocksAsync(Opinion? recordedFromLead = null)
    {
        var filter = Filters();
        overview = (await client.AskAsync(new("overview", filter with { Offset = stockOffset }), lifetime)).Overview
            ?? throw new InvalidDataException("缺少股票汇总。");
        if (stockOffset >= overview.StockCount && stockOffset > 0)
        {
            stockOffset = 0;
            overview = (await client.AskAsync(new("overview", filter), lifetime)).Overview!;
        }
        var industries = new[] { "全部行业" }.Concat(overview.Industries)
            .Append("未分类").Distinct().ToArray();
        IndustryFilter.ItemsSource = industries;
        IndustryFilter.SelectedItem = industries.Contains(filter.Industry) ? filter.Industry : industries[0];
        var previous = selected?.Security.Key;
        var preferred = recordedFromLead?.Security?.Key ?? previous;
        selected = overview.Stocks.FirstOrDefault(x => x.Security.Key == preferred) ?? overview.Stocks.FirstOrDefault();
        if (selected?.Security.Key != previous || mode == "mentions" && selected is not { Mentions: > 0 })
        {
            mode = DefaultMode(selected);
            detailOffset = 0;
            MatrixScroll.ScrollToHorizontalOffset(0);
        }
        if (recordedFromLead is not null)
        {
            if (recordedFromLead.Security is null) mode = "pending";
            else if (selected?.Security.Key == preferred) mode = "timeline";
            detailOffset = 0;
        }

        RenderMatrix();
        PriorStocks.IsEnabled = stockOffset > 0;
        NextStocks.IsEnabled = stockOffset + overview.Stocks.Length < overview.StockCount;
        StockPageText.Text = overview.StockCount == 0 ? "0 只股票" :
            $"{stockOffset + 1}–{stockOffset + overview.Stocks.Length} / {overview.StockCount} 只股票";
        var coverage = overview.Earliest is { } from && overview.Latest is { } to
            ? $"资料覆盖 {from:yyyy-MM-dd} 至 {to:yyyy-MM-dd} · 最近录入 {overview.LastRecorded:MM-dd HH:mm}"
            : overview.StockCount > 0 ? $"当前范围内已识别 {overview.StockCount} 只股票的原帖提及。"
            : "当前范围内暂无股票提及或观点。可在「发言线索」查看已采集的原帖。";
        Status.Text = coverage;
        PendingStocks.Content = overview.Unmatched > 0 ? $"待认领股票（{overview.Unmatched}）" : "待认领股票";
        await RefreshDetailsAsync();
    }

    private void RenderMatrix()
    {
        Matrix.Children.Clear();
        Matrix.ColumnDefinitions.Clear();
        Matrix.RowDefinitions.Clear();
        MatrixLabels.Children.Clear();

        for (var row = 0; row < RowHeights.Length; row++)
            Matrix.RowDefinitions.Add(new() { Height = new(RowHeights[row]) });
        foreach (var (label, row) in new[] { "行业", "股票", "看多", "看空" }.Select((label, row) => (label, row)))
        {
            var border = MatrixCell(label, row, "BrandSurfaceAltBrush");
            border.Height = RowHeights[row];
            MatrixLabels.Children.Add(border);
        }

        var stocks = overview?.Stocks ?? [];
        for (var column = 0; column < stocks.Length; column++)
            Matrix.ColumnDefinitions.Add(new() { Width = new(ColumnWidth) });

        var position = 0;
        foreach (var group in stocks.GroupBy(x => x.Security.Industry))
        {
            var label = group.Key.Length == 0 ? "未分类" : group.Key;
            var heading = MatrixCell(label, 0, "BrandSurfaceAltBrush");
            Grid.SetColumn(heading, position);
            Grid.SetColumnSpan(heading, group.Count());
            Matrix.Children.Add(heading);
            position += group.Count();
        }

        for (var column = 0; column < stocks.Length; column++)
        {
            var stock = stocks[column];
            var active = stock.Security.Key == selected?.Security.Key;
            var panel = new StackPanel();
            var title = MatrixCell($"{stock.Security.Name}\n{stock.Security.Market} · {stock.Security.Code}", 1,
                active ? "BrandAccentPaleBrush" : "BrandSurfaceBrush");
            title.Height = RowHeights[1];
            panel.Children.Add(title);
            foreach (var (count, row, surface, foreground) in new[]
            {
                (stock.Bullish, 2, "BrandAccentSoftBrush", "BrandAccentDarkBrush"),
                (stock.Bearish, 3, "BrandStatusSuccessSurfaceBrush", "BrandStatusSuccessBrush")
            })
            {
                var cell = MatrixCell(count.ToString(), row, count > 0 ? surface : "BrandSurfaceAltBrush", foreground);
                cell.Height = RowHeights[row];
                panel.Children.Add(cell);
            }
            var button = new Button
            {
                Content = panel, Padding = new(0), BorderThickness = active ? new(1.5) : new(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                ToolTip = $"{stock.Security.Name} · 发言提及 {stock.Mentions} 篇 · 看多 {stock.Bullish} 位 · 看空 {stock.Bearish} 位"
            };
            button.SetResourceReference(BorderBrushProperty, "BrandAccentBrush");
            System.Windows.Automation.AutomationProperties.SetName(button, button.ToolTip.ToString());
            button.Click += async (_, _) => await RunAsync(async () =>
            {
                selected = stock;
                mode = DefaultMode(stock);
                detailOffset = 0;
                RenderMatrix();
                await RefreshDetailsAsync();
            });
            Grid.SetRow(button, 1);
            Grid.SetRowSpan(button, 3);
            Grid.SetColumn(button, column);
            Matrix.Children.Add(button);
        }
    }

    private static string DefaultMode(StockSummary? stock) =>
        stock is { Mentions: > 0, Bullish: 0, Bearish: 0 } ? "mentions" : "latest";

    private static Border MatrixCell(string value, int row, string background, string foreground = "BrandTextPrimaryBrush")
    {
        var text = new TextBlock
        {
            Text = value, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, FontSize = row >= 2 ? 16 : 12,
            FontWeight = row == 1 ? FontWeights.Normal : FontWeights.SemiBold, Margin = new(3, 1, 3, 1)
        };
        text.SetResourceReference(ForegroundProperty, foreground);
        var border = new Border { Child = text, BorderThickness = new(0, 0, 1, 1) };
        border.SetResourceReference(Border.BackgroundProperty, background);
        border.SetResourceReference(Border.BorderBrushProperty, "BrandBorderBrush");
        return border;
    }

    private async Task RefreshDetailsAsync()
    {
        var filter = Filters();
        StockMentionsButton.IsEnabled = selected is { Mentions: > 0 };
        StockMentionsButton.Content = selected is { Mentions: > 0 } stock ? $"发言提及（{stock.Mentions}）" : "发言提及";
        EditOpinionButton.Content = mode == "mentions" ? "核对选中发言" : "编辑选中观点";
        if (mode == "mentions" && selected is { } activeStock)
        {
            var request = filter with { Security = activeStock.Security, Offset = detailOffset };
            var reply = await client.AskAsync(new("mentions", request), lifetime);
            var mentions = reply.Mentions ?? throw new InvalidDataException("缺少股票发言提及。");
            if (detailOffset > 0 && detailOffset >= mentions.Total)
            {
                detailOffset = 0;
                mentions = (await client.AskAsync(new("mentions", request with { Offset = 0 }), lifetime)).Mentions!;
            }
            Details.ItemsSource = mentions.Rows.Select(x => new MentionRow(x, AuthorName(x.Account))).ToArray();
            DetailTitle.Text = $"{activeStock.Security.Name} · 原帖提及";
            SetDetailsPage(mentions.Rows.Length, mentions.Total, "暂无发言提及");
            return;
        }
        var global = mode is "pending" or "questions";
        if (global) filter = filter with { Market = null, Industry = null, Search = null };
        filter = filter with { Security = global ? null : selected?.Security, Mode = mode,
            Offset = detailOffset, Days = mode is "timeline" or "questions" or "pending" ? 0 : filter.Days };
        var detail = (await client.AskAsync(new("detail", filter), lifetime)).Detail
            ?? throw new InvalidDataException("缺少观点明细。");
        if (detailOffset > 0 && detailOffset >= detail.Total)
        {
            detailOffset = 0;
            detail = (await client.AskAsync(new("detail", filter with { Offset = 0 }), lifetime)).Detail!;
        }
        Details.ItemsSource = detail.Rows.Select(x => new OpinionRow(x, AuthorName(x.Account), mode == "questions")).ToArray();
        DetailTitle.Text = mode switch
        {
            "pending" => "待认领股票 · 请选择记录并确认标的",
            "questions" => "待核实问题 · 可编辑核实结果",
            _ when selected is null => "请选择股票",
            _ => $"{selected.Security.Name} · {selected.Discussions} 篇实质讨论 · 看多 {selected.Bullish} 位 · 看空 {selected.Bearish} 位" +
                (mode == "timeline" ? " · 历史时间线" : mode == "other" ? " · 中性 / 待确认" : "")
        };
        SetDetailsPage(detail.Rows.Length, detail.Total, "暂无观点");
    }

    private string AuthorName(string account) => authors.FirstOrDefault(a => a.Account == account)?.Name ?? account;

    private void SetDetailsPage(int count, int total, string emptyLabel)
    {
        DetailPageText.Text = total == 0 ? emptyLabel : $"{detailOffset + 1}–{detailOffset + count} / {total}";
        PriorDetails.IsEnabled = detailOffset > 0;
        NextDetails.IsEnabled = detailOffset + count < total;
    }

    private abstract record SourceRow(string Author, PostBodyRequest Post, DateTimeOffset? PublishedAt)
    {
        public string Time => PublishedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm") ?? "待核对";
        public bool HasLink => Post.Url.Length > 0;
    }

    private sealed record MentionRow(StockMention Mention, string Author)
        : SourceRow(Author, new(Mention.Account, Mention.Url), Mention.PublishedAt)
    {
        public string Direction => "原帖提及";
        public string Content => WatchInput.DisplayStockTags(Mention.Excerpt);
    }

    private sealed record OpinionRow(Opinion Record, string Author, bool Questions)
        : SourceRow(Author, new(Record.Account, Record.Url, Record.PostBodyRevision), Record.PublishedAt)
    {
        public string Direction => Record.Attribution switch
        {
            Attribution.Mention => "顺带提及",
            Attribution.Repost => "转述 / 转发",
            _ => Record.Stance switch
            {
                Stance.Bullish => "看多", Stance.Bearish => "看空", Stance.Neutral => "中性", _ => "待确认"
            }
        };
        public string Content
        {
            get
            {
                var question = Record.Question.Length > 0 ? $"\n待核实  {Record.Question}" : "";
                var finding = Record.Finding.Length > 0 ? $"\n核实结果  {Record.Finding}" : "";
                var title = Questions && Record.Security is {} security ? $"{security.Name}  " : "";
                return title + Record.Content + question + finding;
            }
        }
    }

    private async void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loaded && !updating) await ReloadFiltersAsync();
    }

    private Task ReloadFiltersAsync() => RunAsync(async () =>
    {
        stockOffset = detailOffset = 0;
        await RefreshStocksAsync();
    });

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async void Search(object sender, RoutedEventArgs e) => await ReloadFiltersAsync();
    private async void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await ReloadFiltersAsync(); }
    }

    private Task SetModeAsync(string next) => RunAsync(async () =>
    {
        mode = next;
        detailOffset = 0;
        await RefreshDetailsAsync();
    });

    private async void LatestClicked(object sender, RoutedEventArgs e) => await SetModeAsync("latest");
    private async void MentionsClicked(object sender, RoutedEventArgs e) => await SetModeAsync("mentions");
    private async void TimelineClicked(object sender, RoutedEventArgs e) => await SetModeAsync("timeline");
    private async void OtherClicked(object sender, RoutedEventArgs e) => await SetModeAsync("other");
    private async void PendingClicked(object sender, RoutedEventArgs e) => await SetModeAsync("pending");
    private async void QuestionsClicked(object sender, RoutedEventArgs e) => await SetModeAsync("questions");
    private async void PriorStockPage(object sender, RoutedEventArgs e) => await MoveStocksAsync(-WatchProtocol.StocksPerPage);
    private async void NextStockPage(object sender, RoutedEventArgs e) => await MoveStocksAsync(WatchProtocol.StocksPerPage);
    private async void PriorDetailPage(object sender, RoutedEventArgs e) => await MoveDetailsAsync(-WatchProtocol.OpinionsPerPage);
    private async void NextDetailPage(object sender, RoutedEventArgs e) => await MoveDetailsAsync(WatchProtocol.OpinionsPerPage);

    private Task MoveStocksAsync(int delta) => RunAsync(async () =>
    {
        stockOffset = Math.Max(0, stockOffset + delta);
        await RefreshStocksAsync();
    });

    private Task MoveDetailsAsync(int delta) => RunAsync(async () =>
    {
        detailOffset = Math.Max(0, detailOffset + delta);
        await RefreshDetailsAsync();
    });

    private async void ManageAuthors(object sender, RoutedEventArgs e)
    {
        var dialog = new AuthorEditor(Window.GetWindow(this), authors, client, lifetime);
        dialog.ShowDialog();
        await RunAsync(RefreshAsync);
    }

    private async void EditOpinion(object sender, RoutedEventArgs e)
    {
        if (Details.SelectedItem is OpinionRow row) await RunAsync(() => OpenEditorAsync(row.Record));
        else if (Details.SelectedItem is MentionRow mention && selected is { } stock)
            await RunAsync(() => OpenMentionAsync(mention.Mention, stock.Security));
        else Status.Text = mode == "mentions" ? "请先选中一条发言。" : "请先在明细表中选中一条观点。";
    }

    private async Task OpenMentionAsync(StockMention mention, Security stock)
    {
        var directory = (await client.AskAsync(new("catalog"), lifetime)).Securities ?? [];
        var body = await client.ReadPostBodyAsync(new(mention.Account, mention.Url), lifetime);
        var lead = new PostLead(mention.Account, mention.Url, mention.FirstSeen,
            Content: mention.Excerpt, PublishedAt: mention.PublishedAt);
        var dialog = new OpinionEditor(Window.GetWindow(this), authors, directory, null, client, lifetime, lead, body, stock);
        if (dialog.ShowDialog() == true && dialog.SavedOpinion is { } saved)
            await RefreshSavedOpinionAsync(saved);
    }

    private async Task OpenEditorAsync(Opinion existing)
    {
        if (authors.Length == 0) throw new ArgumentException("请先在观察名单添加作者。");
        var directory = (await client.AskAsync(new("catalog"), lifetime)).Securities ?? [];
        var body = existing.Url.StartsWith($"https://xueqiu.com/{existing.Account}/", StringComparison.Ordinal)
            ? await client.ReadPostBodyAsync(new(existing.Account, existing.Url, existing.PostBodyRevision), lifetime) : null;
        var dialog = new OpinionEditor(Window.GetWindow(this), authors, directory, existing, client, lifetime,
            postBody: body);
        if (dialog.ShowDialog() == true)
        {
            await RefreshStocksAsync();
            Status.Text = "观点已保存。  " + Status.Text;
        }
    }

    private async void ShowPostBody(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Details.SelectedItem is not SourceRow { HasLink: true } row)
            throw new ArgumentException("请先选择一条带雪球原帖链接的记录。");
        await PostBodyViewer.OpenAsync(Window.GetWindow(this), client, row.Post, row.PublishedAt, lifetime);
    });

    private void OpenSource(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SourceRow { HasLink: true } row }) return;
        try { Process.Start(new ProcessStartInfo(WatchInput.SourceLink(row.Post.Url)) { UseShellExecute = true }); }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception)
        { Status.Text = "无法打开雪球原帖，请检查浏览器或链接。"; }
    }
}
