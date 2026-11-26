using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Ipc;
using EyePath = System.Windows.Shapes.Path;

namespace CRS.Plugins.XueqiuWatch.Wpf;

public partial class WatchPage : UserControl
{
    private readonly WorkerWatchClient client;
    private readonly CancellationToken lifetime;
    private readonly MarketStockSection[] marketSections;
    private Author[] authors = [];
    private StockSummary? selected;
    private bool loaded;
    private bool updating;
    private int detailOffset;
    private string mode = "latest";

    internal WatchPage(WorkerWatchClient client, CancellationToken lifetime)
    {
        this.client = client;
        this.lifetime = lifetime;
        InitializeComponent();
        var eye = new EyePath { Data = StockVisibilityIcons.Visible, Width = 16, Height = 16,
            StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform };
        eye.SetResourceReference(EyePath.StrokeProperty, "BrandTextPrimaryBrush");
        StockVisibilityButton.Content = eye;
        Details.RowHeight = double.NaN;
        PeriodFilter.ItemsSource = new[] { "近 30 天", "近 7 天" };
        PeriodFilter.SelectedIndex = 0;
        marketSections = WatchProtocol.MarketGroups.Select(market => new MarketStockSection { Market = market }).ToArray();
        foreach (var market in marketSections)
        {
            MarketTabs.Items.Add(new TabItem { Header = market.Market, Content = market });
            market.PageRequested += async offset => await RunAsync(() => RefreshStocksAsync(requestedOffset: offset));
            market.StockSelected += async stock => await SelectStockAsync(market, stock);
            market.StockHidden += async stock => await HideStockAsync(stock);
        }
        MarketTabs.SelectedIndex = 0;
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

    private MarketStockSection ActiveMarket => (MarketStockSection)((TabItem)MarketTabs.SelectedItem).Content;

    private ReviewFilter Filters() => new(PeriodFilter.SelectedIndex == 1 ? 7 : 30,
        (AuthorFilter.SelectedItem as Author)?.Account is { Length: > 0 } account ? account : null,
        ActiveMarket.Market, Search: StockSearch.Text);

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

    private async Task RefreshStocksAsync(Opinion? recordedFromLead = null, int? requestedOffset = null)
    {
        var market = ActiveMarket;
        var offset = requestedOffset ?? market.Offset;
        var filter = Filters() with { SortByActivity = true };
        var overview = (await client.AskAsync(new("overview", filter with { Offset = offset }), lifetime)).Overview
            ?? throw new InvalidDataException("缺少股票汇总。");
        if (offset >= overview.StockCount && offset > 0)
        {
            offset = Math.Max(0, (overview.StockCount - 1) / WatchProtocol.StocksPerPage * WatchProtocol.StocksPerPage);
            overview = (await client.AskAsync(new("overview", filter with { Offset = offset }), lifetime)).Overview
                ?? throw new InvalidDataException("缺少股票汇总。");
        }
        var previous = selected?.Security.Key;
        var preferred = recordedFromLead?.Security?.Key ?? market.SelectedKey;
        selected = overview.Stocks.FirstOrDefault(x => x.Security.Key == preferred) ?? overview.Stocks.FirstOrDefault();
        if (selected?.Security.Key != previous || mode == "mentions" && selected is not { Mentions: > 0 })
        {
            mode = DefaultMode(selected);
            detailOffset = 0;
        }
        if (recordedFromLead is not null)
        {
            if (recordedFromLead.Security is null) mode = "pending";
            else if (selected?.Security.Key == preferred) mode = "timeline";
            detailOffset = 0;
        }

        market.ShowPage(overview, offset, selected?.Security.Key);
        StockVisibilityButton.IsEnabled = overview.StockVisibilitySupported;
        StockVisibilityButton.ToolTip = overview.HiddenStockCount is > 0
            ? $"管理股票显示（已隐藏 {overview.HiddenStockCount} 只）" : "管理股票显示";
        var coverage = overview.StockCount == 0 && overview.HiddenStockCount is > 0
            ? "当前范围内没有可显示的股票。可通过眼睛图标管理股票显示。"
            : overview.Earliest is { } from && overview.Latest is { } to
            ? $"资料覆盖 {from:yyyy-MM-dd} 至 {to:yyyy-MM-dd} · 最近录入 {overview.LastRecorded:MM-dd HH:mm}"
            : overview.StockCount == 0 ? "可在「发言线索」查看已采集的原帖。" : "";
        Status.Text = coverage;
        PendingStocks.Content = overview.Unmatched > 0 ? $"待认领股票（{overview.Unmatched}）" : "待认领股票";
        await RefreshDetailsAsync();
    }

    private Task SelectStockAsync(MarketStockSection market, StockSummary stock) => RunAsync(async () =>
    {
        selected = stock;
        market.SelectStock(stock.Security.Key);
        mode = DefaultMode(stock);
        detailOffset = 0;
        await RefreshDetailsAsync();
    });

    private Task HideStockAsync(Security stock) => RunAsync(async () =>
    {
        await client.SetStockHiddenAsync(stock, true, lifetime);
        await RefreshStocksAsync();
        Status.Text = $"已隐藏「{stock.Name}」。{Status.Text}";
    });

    private static string DefaultMode(StockSummary? stock) =>
        stock is { Mentions: > 0, Bullish: 0, Bearish: 0 } ? "mentions" : "latest";

    private async void ShowStockVisibility(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = new StockVisibilityEditor(Window.GetWindow(this), client, lifetime, ActiveMarket.Market);
        dialog.ShowDialog();
        if (dialog.HasChanges) await RefreshStocksAsync();
    });

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
        if (global) filter = filter with { Market = null, Search = null };
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

    private async void MarketChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded || e.Source != MarketTabs) return;
        await RunAsync(async () =>
        {
            selected = null;
            mode = "latest";
            detailOffset = 0;
            Details.ItemsSource = null;
            await RefreshStocksAsync();
        });
    }

    private Task ReloadFiltersAsync() => RunAsync(async () =>
    {
        detailOffset = 0;
        foreach (var market in marketSections) market.ResetPage();
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
    private async void PriorDetailPage(object sender, RoutedEventArgs e) => await MoveDetailsAsync(-WatchProtocol.OpinionsPerPage);
    private async void NextDetailPage(object sender, RoutedEventArgs e) => await MoveDetailsAsync(WatchProtocol.OpinionsPerPage);

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
