using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CRS.Plugin.Wpf;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>顶部分类导航与全宽内容；按需创建当前页，不执行采集或金融运算。</summary>
public sealed class NationalTeamWorkspacePage : UserControl, IPluginShellHeader
{
    private readonly IReadOnlyDictionary<string, NationalTeamWorkspaceItem> items;
    private readonly ContentControl content = new() { Name = "ResearchContent", HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock location = new() { Text = "选择研究功能", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19 };
    private readonly Border noticePanel;
    private readonly PluginSectionNavigation navigation;
    private readonly Grid root;
    private readonly Border header;
    public FrameworkElement HeaderContent=>header;
    public string? SelectedRouteKey { get; private set; }
    public int CategoryCount { get; }

    public NationalTeamWorkspacePage(IReadOnlyList<NationalTeamWorkspaceItem> entries)
    {
        NationalTeamWorkspaceCatalog.Validate(entries);
        items = entries.ToDictionary(x => x.RouteKey, StringComparer.Ordinal);
        var groups = NationalTeamWorkspaceCatalog.Categories.Where(g => entries.Any(e => e.CategoryKey == g.Key)).ToArray();
        CategoryCount = groups.Length;
        navigation = new(groups.Select(g => new PluginNavigationCategory(g.Key, g.Title)).ToArray(),
            entries.Select(e => new PluginNavigationDestination(e.RouteKey, e.CategoryKey, e.Title, e.EvidenceNotice)).ToArray());
        navigation.RouteRequested += OpenRoute;
        navigation.Name = "ResearchTopNavigation";
        location.SetResourceReference(TextBlock.ForegroundProperty, "BrandTextSecondaryBrush");
        notice.SetResourceReference(TextBlock.ForegroundProperty, "BrandTextSecondaryBrush");
        SetResourceReference(BackgroundProperty, "BrandCanvasBrush");
        SetResourceReference(ForegroundProperty, "BrandTextPrimaryBrush");
        root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(navigation);
        Grid.SetColumn(location, 1); toolbar.Children.Add(location);
        var help = new Button { Name = "ResearchNoticeToggle", Content = "研究说明", FontSize = 12, MinWidth = 68,
            Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(help, "展开或收起当前研究资料说明");
        header = new Border { Name = "ResearchToolbar", Child = toolbar, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = new Thickness(4), Margin = new Thickness(16, 8, 16, 0) };
        header.SetResourceReference(Border.BackgroundProperty, "BrandSurfaceBrush");
        header.SetResourceReference(Border.BorderBrushProperty, "BrandBorderBrush");
        Grid.SetColumn(help, 2); toolbar.Children.Add(help); root.Children.Add(header);

        notice.Text = "ETF份额、机构持仓和市场成交属于不同证据链；市场成交用于观察冷热度，不推断具名机构的买卖。";
        noticePanel = new Border { Name = "ResearchNotice", Child = notice, Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(20, 6, 20, 0) };
        noticePanel.SetResourceReference(Border.BackgroundProperty, "BrandSurfaceBrush");
        help.Click += (_, _) => noticePanel.Visibility = noticePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(noticePanel, 1); root.Children.Add(noticePanel);
        Grid.SetRow(content, 2); root.Children.Add(content);
        Content = root;
        var welcome = new StackPanel { MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        welcome.Children.Add(new TextBlock { Text = "从上方选择研究内容", FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var hint = new TextBlock { Text = "资金与持仓、行业与估值分开查看。进入功能后按需读取资料，页面切换不会触发采集。", FontSize = 13, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "BrandTextSecondaryBrush");
        welcome.Children.Add(hint); content.Content = welcome;
    }

    public void SetHeaderHosted(bool hosted)
    {
        // 同一个菜单实例在顶栏与独立页之间移交，避免两份选中态及重复事件。
        if(hosted)root.Children.Remove(header);
        else if(!root.Children.Contains(header))root.Children.Add(header);
        location.Visibility=hosted?Visibility.Collapsed:Visibility.Visible;
        header.Margin=hosted?new Thickness():new Thickness(16,8,16,0);
        header.Padding=hosted?new Thickness():new Thickness(4);
        header.BorderThickness=hosted?new Thickness():new Thickness(1);
        header.HorizontalAlignment=hosted?HorizontalAlignment.Left:HorizontalAlignment.Stretch;
        header.ToolTip=hosted?location.Text:null;
    }

    public void OpenRoute(string routeKey)
    {
        if (!items.TryGetValue(routeKey, out var item)) throw new InvalidDataException("国家队研究功能路由不存在。");
        if (SelectedRouteKey == routeKey) return;
        // 新页面构造成功后再提交正文、菜单及当前位置；失败时保留原页面和选中态。
        var page = item.CreateView() ?? throw new InvalidDataException("插件研究功能无法创建页面。");
        content.Content = page;
        SelectedRouteKey = routeKey;
        navigation.Select(routeKey);
        var category = NationalTeamWorkspaceCatalog.Categories.Single(x => x.Key == item.CategoryKey);
        location.Text = category.Title + " / " + item.Title;
        location.ToolTip = location.Text;
        header.ToolTip=location.Visibility==Visibility.Collapsed?location.Text:null;
        notice.Text = item.EvidenceNotice + " 市场成交用于观察整体冷热，不直接归因具名主体。";
    }
}
