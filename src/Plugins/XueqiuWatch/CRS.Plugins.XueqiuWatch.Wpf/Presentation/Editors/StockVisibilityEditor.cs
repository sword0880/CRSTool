using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CRS.Plugins.XueqiuWatch.Core;
using EyePath = System.Windows.Shapes.Path;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class StockVisibilityEditor : WatchEditor
{
    private readonly WorkerWatchClient client;
    private readonly CancellationToken lifetime;
    private readonly ObservableCollection<StockRow> rows = [];
    private readonly TabControl marketTabs = new()
    {
        Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0),
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly StackPanel marketContent = new();
    private readonly TextBlock page = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
    private readonly Button prior;
    private readonly Button next;
    private int offset;
    internal bool HasChanges { get; private set; }

    internal StockVisibilityEditor(Window? owner, WorkerWatchClient client, CancellationToken lifetime, string market)
        : base(owner, "股票显示设置", 540)
    {
        this.client = client;
        this.lifetime = lifetime;
        var stocks = new DataGrid
        {
            Height = 352, RowHeight = 36, IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single, ItemsSource = rows
        };
        stocks.SetResourceReference(StyleProperty, "UiDenseDataGrid");
        stocks.Columns.Add(new DataGridTextColumn { Header = "股票名称", Binding = new Binding(nameof(StockRow.Name)), Width = new(1, DataGridLengthUnitType.Star) });
        stocks.Columns.Add(new DataGridTextColumn { Header = "市场", Binding = new Binding(nameof(StockRow.Market)), Width = 76 });
        stocks.Columns.Add(new DataGridTextColumn { Header = "代码", Binding = new Binding(nameof(StockRow.Code)), Width = 78 });
        stocks.Columns.Add(new DataGridTemplateColumn
        {
            Header = "显示", Width = 58, CanUserSort = false, CellTemplate = VisibilityCell()
        });
        AutomationProperties.SetName(stocks, "当前市场股票");
        marketContent.Children.Add(stocks);

        var pagination = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 6, 0, 0) };
        prior = AsyncAction("上一页", () => BrowseAsync(Math.Max(0, offset - WatchProtocol.VisibilityStocksPerPage)));
        next = AsyncAction("下一页", () => BrowseAsync(offset + WatchProtocol.VisibilityStocksPerPage));
        next.IsEnabled = prior.IsEnabled = false;
        pagination.Children.Add(page);
        pagination.Children.Add(prior);
        pagination.Children.Add(next);
        marketContent.Children.Add(pagination);

        AutomationProperties.SetName(marketTabs, "股票显示市场");
        var tabs = WatchProtocol.MarketGroups.Select(name => new TabItem { Header = name }).ToArray();
        foreach (var tab in tabs) marketTabs.Items.Add(tab);
        var active = tabs.FirstOrDefault(tab => Equals(tab.Header, market)) ?? tabs[0];
        marketTabs.SelectedItem = active;
        active.Content = marketContent;
        marketTabs.SelectionChanged += MarketChanged;
        Fields.Children.Add(marketTabs);
        Finish(Action("关闭", Close));
        Loaded += async (_, _) => await BrowseAsync(0);
    }

    private async void MarketChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != marketTabs || marketTabs.SelectedItem is not TabItem selected) return;
        foreach (TabItem previous in e.RemovedItems) previous.Content = null;
        selected.Content = marketContent;
        rows.Clear();
        page.Text = "";
        next.IsEnabled = prior.IsEnabled = false;
        await BrowseAsync(0);
    }

    private DataTemplate VisibilityCell()
    {
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(FrameworkElement.WidthProperty, 30d);
        button.SetValue(FrameworkElement.HeightProperty, 27d);
        button.SetValue(FrameworkElement.MinWidthProperty, 0d);
        button.SetValue(FrameworkElement.MinHeightProperty, 0d);
        button.SetValue(Control.PaddingProperty, new Thickness(4));
        button.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        button.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(StockRow.Action)));
        button.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(StockRow.Action)));
        button.AddHandler(Button.ClickEvent, new RoutedEventHandler(async (sender, _) =>
        {
            if (sender is Button { DataContext: StockRow row }) await ToggleAsync(row);
        }));

        var icon = new FrameworkElementFactory(typeof(EyePath));
        icon.SetBinding(EyePath.DataProperty, new Binding(nameof(StockRow.Icon)));
        icon.SetBinding(EyePath.StrokeProperty, new Binding(nameof(Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
        });
        icon.SetValue(FrameworkElement.WidthProperty, 16d);
        icon.SetValue(FrameworkElement.HeightProperty, 16d);
        icon.SetValue(EyePath.StrokeThicknessProperty, 1.5);
        icon.SetValue(EyePath.StrokeLineJoinProperty, PenLineJoin.Round);
        icon.SetValue(EyePath.StretchProperty, Stretch.Uniform);
        button.AppendChild(icon);
        return new DataTemplate { VisualTree = button };
    }

    private Task BrowseAsync(int requestedOffset) => WithFeedbackAsync(async () =>
    {
        var market = ((TabItem)marketTabs.SelectedItem).Header as string;
        async Task<StockVisibilityPage> ReadAsync(int start) =>
            (await client.AskAsync(new("stock-visibility", new(Market: market, Offset: start)), lifetime)).StockVisibility
                ?? throw new InvalidDataException("缺少股票显示清单。");

        var result = await ReadAsync(requestedOffset);
        if (requestedOffset > 0 && requestedOffset >= result.Total)
        {
            requestedOffset = Math.Max(0,
                (result.Total - 1) / WatchProtocol.VisibilityStocksPerPage * WatchProtocol.VisibilityStocksPerPage);
            result = await ReadAsync(requestedOffset);
        }
        offset = requestedOffset;
        rows.Clear();
        foreach (var row in result.Rows) rows.Add(new(row.Security, row.Hidden));
        prior.IsEnabled = offset > 0;
        next.IsEnabled = offset + result.Rows.Length < result.Total;
        prior.Visibility = next.Visibility = result.Total > WatchProtocol.VisibilityStocksPerPage
            ? Visibility.Visible : Visibility.Collapsed;
        page.Text = result.Total == 0 ? "暂无股票" : result.Total <= WatchProtocol.VisibilityStocksPerPage
            ? $"共 {result.Total} 只股票" : $"{offset + 1}–{offset + rows.Count} / {result.Total} 只股票";
    }, lifetime);

    private Task ToggleAsync(StockRow row) => WithFeedbackAsync(async () =>
    {
        var index = rows.IndexOf(row);
        if (index < 0) return;
        var hidden = !row.Hidden;
        await client.SetStockHiddenAsync(row.Security, hidden, lifetime);
        rows[index] = row with { Hidden = hidden };
        HasChanges = true;
    }, lifetime);

    private sealed record StockRow(Security Security, bool Hidden)
    {
        public string Name => Security.Name;
        public string Market => Security.Market;
        public string Code => Security.Code;
        public string Action => Hidden ? $"显示「{Name}」" : $"隐藏「{Name}」";
        public Geometry Icon => Hidden ? StockVisibilityIcons.Hidden : StockVisibilityIcons.Visible;
    }
}
