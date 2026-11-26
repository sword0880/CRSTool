using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Wpf;

public partial class MarketStockSection : UserControl
{
    private const double ColumnWidth = 112;
    private static readonly double[] RowHeights = [42, 23, 23];

    internal string Market { get; set; } = "";
    internal string? SelectedKey { get; private set; }
    internal int Offset { get; private set; }
    internal event Action<int>? PageRequested;
    internal event Action<StockSummary>? StockSelected;
    internal event Action<Security>? StockHidden;

    public MarketStockSection()
    {
        InitializeComponent();
        foreach (var (label, row) in new[] { "股票", "看多", "看空" }.Select((label, row) => (label, row)))
        {
            var cell = MatrixCell(label, row, "BrandSurfaceAltBrush");
            Labels.Children.Add(cell);
            Matrix.RowDefinitions.Add(new() { Height = new(RowHeights[row]) });
        }
    }

    internal void ResetPage()
    {
        Offset = 0;
        MatrixScroll.ScrollToHorizontalOffset(0);
    }

    internal void ShowPage(WatchOverview overview, int offset, string? selectedKey)
    {
        if (offset != Offset) MatrixScroll.ScrollToHorizontalOffset(0);
        Offset = offset;
        PageText.Text = overview.StockCount switch
        {
            0 => "",
            <= WatchProtocol.StocksPerPage => $"{overview.StockCount} 只股票",
            _ => $"{offset + 1}–{offset + overview.Stocks.Length} / {overview.StockCount} 只股票"
        };
        Pager.Visibility = overview.StockCount > WatchProtocol.StocksPerPage ? Visibility.Visible : Visibility.Collapsed;
        PriorPage.IsEnabled = offset > 0;
        NextPage.IsEnabled = offset + overview.Stocks.Length < overview.StockCount;
        var hasStocks = overview.Stocks.Length > 0;
        Labels.Visibility = MatrixScroll.Visibility = hasStocks ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Visibility = hasStocks ? Visibility.Collapsed : Visibility.Visible;

        Matrix.Children.Clear();
        Matrix.ColumnDefinitions.Clear();
        foreach (var stock in overview.Stocks)
        {
            var button = StockButton(stock, overview.StockVisibilitySupported);
            Grid.SetRowSpan(button, RowHeights.Length);
            Grid.SetColumn(button, Matrix.ColumnDefinitions.Count);
            Matrix.ColumnDefinitions.Add(new() { Width = new(ColumnWidth) });
            Matrix.Children.Add(button);
        }
        SelectStock(selectedKey);
    }

    internal void SelectStock(string? key)
    {
        SelectedKey = key;
        foreach (Button button in Matrix.Children)
        {
            var active = string.Equals(button.Tag as string, key, StringComparison.Ordinal);
            button.BorderThickness = active ? new(1.5) : new(0);
            if (button.Content is StackPanel panel && panel.Children[0] is Border name)
                name.SetResourceReference(Border.BackgroundProperty, active ? "BrandAccentPaleBrush" : "BrandSurfaceBrush");
        }
    }

    private Button StockButton(StockSummary stock, bool visibilitySupported)
    {
        var panel = new StackPanel();
        panel.Children.Add(MatrixCell($"{stock.Security.Name}\n{stock.Security.Market} · {stock.Security.Code}", 0, "BrandSurfaceBrush"));
        foreach (var (count, row, surface, foreground) in new[]
        {
            (stock.Bullish, 1, "BrandAccentSoftBrush", "BrandAccentDarkBrush"),
            (stock.Bearish, 2, "BrandStatusSuccessSurfaceBrush", "BrandStatusSuccessBrush")
        })
            panel.Children.Add(MatrixCell(count.ToString(), row, count > 0 ? surface : "BrandSurfaceAltBrush",
                count > 0 ? foreground : "BrandTextSecondaryBrush"));

        var label = $"{stock.Security.Name} · 发言提及 {stock.Mentions} 篇 · 看多 {stock.Bullish} 位 · 看空 {stock.Bearish} 位";
        var button = new Button
        {
            Tag = stock.Security.Key, Content = panel, Padding = new(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, ToolTip = label
        };
        if (visibilitySupported)
        {
            var hide = new MenuItem { Header = "隐藏此股票" };
            hide.Click += (_, _) => StockHidden?.Invoke(stock.Security);
            button.ContextMenu = new ContextMenu { Items = { hide } };
        }
        button.SetResourceReference(BorderBrushProperty, "BrandAccentBrush");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => StockSelected?.Invoke(stock);
        return button;
    }

    private static Border MatrixCell(string value, int row, string background, string foreground = "BrandTextPrimaryBrush")
    {
        var text = new TextBlock
        {
            Text = value, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, FontSize = row == 0 ? 12 : 11, Margin = new(3, 1, 3, 1)
        };
        text.SetResourceReference(ForegroundProperty, foreground);
        var border = new Border { Child = text, Height = RowHeights[row], BorderThickness = new(0, 0, 1, 1) };
        border.SetResourceReference(Border.BackgroundProperty, background);
        border.SetResourceReference(Border.BorderBrushProperty, "BrandBorderBrush");
        return border;
    }

    private void PreviousPage(object sender, RoutedEventArgs e) => PageRequested?.Invoke(Offset - WatchProtocol.StocksPerPage);

    private void FollowingPage(object sender, RoutedEventArgs e) => PageRequested?.Invoke(Offset + WatchProtocol.StocksPerPage);
}
