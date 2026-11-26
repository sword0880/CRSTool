using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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
    private int detailPageSize = WatchProtocol.DetailRowsPerPage;

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
        await RunAsync(RefreshAsync);
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
        var options = new[] { new Author("", "全部作者") }.Concat(authors).ToArray();
        AuthorFilter.ItemsSource = options;
        AuthorFilter.SelectedItem = options.FirstOrDefault(x => x.Account == account) ?? options[0];
        await RefreshStocksAsync();
        await RefreshLeadCountAsync();
    }

    private async Task RefreshLeadCountAsync()
    {
        try
        {
            var leads = (await client.AskAsync(new("leads"), lifetime)).Leads;
            SetLeadCount(leads?.Total ?? 0);
        }
        catch (ArgumentException error) when (error.Message == "未知的观点追踪操作。")
        {
            // 旧版 Worker 未提供线索查询时，股票观点仍可刷新。
            SetLeadCount(0);
        }
    }

    private void SetLeadCount(int count)
    {
        var label = count > 0 ? $"发言线索（{count}）" : "发言线索";
        LeadsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LeadsCount.Text = count.ToString();
        LeadsButton.ToolTip = label;
        AutomationProperties.SetName(LeadsButton, label);
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
        if (selected?.Security.Key != previous || recordedFromLead is not null) detailOffset = 0;

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
        await RefreshDetailsAsync();
    }

    private Task SelectStockAsync(MarketStockSection market, StockSummary stock) => RunAsync(async () =>
    {
        selected = stock;
        market.SelectStock(stock.Security.Key);
        detailOffset = 0;
        await RefreshDetailsAsync();
    });

    private Task HideStockAsync(Security stock) => RunAsync(async () =>
    {
        await client.SetStockHiddenAsync(stock, true, lifetime);
        await RefreshStocksAsync();
        Status.Text = $"已隐藏「{stock.Name}」。{Status.Text}";
    });

    private async void ShowStockVisibility(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = new StockVisibilityEditor(Window.GetWindow(this), client, lifetime, ActiveMarket.Market);
        dialog.ShowDialog();
        if (dialog.HasChanges) await RefreshStocksAsync();
    });

    private async Task RefreshDetailsAsync()
    {
        MentionAuthorSummary.Visibility = Visibility.Collapsed;
        PostBodyButton.IsEnabled = false;
        if (selected is not { } activeStock)
        {
            Details.ItemsSource = null;
            DetailTitle.Text = "请选择股票";
            SetDetailsPage(0, 0, "请选择股票");
            return;
        }

        var request = Filters() with { Security = activeStock.Security, Offset = detailOffset,
            MentionPageSize = WatchProtocol.DetailRowsPerPage };
        var reply = await client.AskAsync(new("mentions", request), lifetime);
        var mentions = reply.Mentions ?? throw new InvalidDataException("缺少股票发言提及。");
        if (detailOffset > 0 && detailOffset >= mentions.Total)
        {
            detailOffset = 0;
            mentions = (await client.AskAsync(new("mentions", request with { Offset = 0 }), lifetime)).Mentions
                ?? throw new InvalidDataException("缺少股票发言提及。");
        }

        Details.ItemsSource = mentions.Rows.Select(x => new MentionRow(x, AuthorName(x.Account))).ToArray();
        DetailTitle.Text = $"{activeStock.Security.Name} · 原帖提及";
        if (mentions.Total > 0)
        {
            MentionAuthorCounts.Text = mentions.AuthorCounts is { Length: > 0 } counts
                ? "按作者统计  " + string.Join("  ·  ", counts.Select(x => $"{AuthorName(x.Account)} {x.Count} 篇"))
                : "更新 Worker 后可查看按作者统计。";
            MentionAuthorSummary.ScrollToTop();
            MentionAuthorSummary.Visibility = Visibility.Visible;
            PostBodyButton.IsEnabled = true;
        }
        SetDetailsPage(mentions.Rows.Length, mentions.Total, "暂无发言提及");
    }

    private string AuthorName(string account) => authors.FirstOrDefault(a => a.Account == account)?.Name ?? account;

    private void SetDetailsPage(int count, int total, string emptyLabel)
    {
        if (detailOffset == 0 || detailOffset + count < total)
            detailPageSize = count > 0 ? count : WatchProtocol.DetailRowsPerPage;
        DetailPageText.Text = total == 0 ? emptyLabel : $"{detailOffset + 1}–{detailOffset + count} / {total}";
        var paged = total > count || detailOffset > 0;
        PriorDetails.Visibility = NextDetails.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
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

    private async void PriorDetailPage(object sender, RoutedEventArgs e) => await MoveDetailsAsync(-detailPageSize);
    private async void NextDetailPage(object sender, RoutedEventArgs e) => await MoveDetailsAsync(detailPageSize);

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

    private async void ShowPostBody(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Details.SelectedItem is not SourceRow { HasLink: true } row)
            throw new ArgumentException("请先选择一条带雪球原帖链接的记录。");
        await PostBodyViewer.OpenAsync(Window.GetWindow(this), client, row.Post, row.PublishedAt, lifetime);
    });

    private void ContentMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is TextBlock { DataContext: SourceRow { HasLink: true } row })
        {
            e.Handled = true;
            OpenSource(row);
        }
    }

    private void ContentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBlock { DataContext: SourceRow { HasLink: true } row })
        {
            e.Handled = true;
            if (!e.IsRepeat) OpenSource(row);
        }
    }

    private void OpenSource(SourceRow row)
    {
        try { Process.Start(new ProcessStartInfo(WatchInput.SourceLink(row.Post.Url)) { UseShellExecute = true }); }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception)
        { Status.Text = "无法打开雪球原帖，请检查浏览器或链接。"; }
    }
}