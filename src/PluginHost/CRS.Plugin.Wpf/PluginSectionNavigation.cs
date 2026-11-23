using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace CRS.Plugin.Wpf;

public sealed record PluginNavigationCategory(string Key, string Title);
public sealed record PluginNavigationDestination(string RouteKey, string CategoryKey, string Title, string Description);

/// <summary>跨插件共用的顶部分类菜单；只提出导航请求，不创建业务页面、不查询或保存数据。</summary>
public sealed class PluginSectionNavigation : UserControl
{
    private readonly Dictionary<string, MenuItem> categories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PluginNavigationDestination Destination, MenuItem Item)> destinations = new(StringComparer.Ordinal);
    public event Action<string>? RouteRequested;
    public string? SelectedRouteKey { get; private set; }

    public PluginSectionNavigation(IReadOnlyList<PluginNavigationCategory> groups, IReadOnlyList<PluginNavigationDestination> entries)
    {
        if (groups.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != groups.Count ||
            entries.Select(x => x.RouteKey).Distinct(StringComparer.Ordinal).Count() != entries.Count ||
            entries.Any(x => !groups.Any(g => g.Key == x.CategoryKey)))
            throw new ArgumentException("插件导航分类或目的地重复、未登记。");
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/CRS.Plugin.Wpf;component/Themes/PluginNavigation.xaml", UriKind.Relative) });
        var row = new Menu { Background=System.Windows.Media.Brushes.Transparent,BorderThickness=new Thickness(),Padding=new Thickness() };
        foreach (var group in groups)
        {
            var members = entries.Where(x => x.CategoryKey == group.Key).ToArray();
            if (members.Length == 0) continue;
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = group.Title, VerticalAlignment = VerticalAlignment.Center });
            var chevron = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M 0,0 L 4,4 L 8,0"),
                StrokeThickness = 1.5, Width = 8, Height = 5, Margin = new Thickness(10, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "BrandTextSecondaryBrush"); label.Children.Add(chevron);
            var category = new MenuItem { Header = label, Style = (Style)Resources["PluginSectionHeader"], Margin = new Thickness(0, 0, 4, 0) };
            AutomationProperties.SetName(category, group.Title + "菜单");
            AutomationProperties.SetHelpText(category, "按Enter、向下键或F4打开功能菜单。");
            foreach (var entry in members)
            {
                var item = new MenuItem { Header = entry.Title, ToolTip = entry.Description,
                    Style = (Style)Resources["PluginDestinationItem"] };
                AutomationProperties.SetName(item, entry.Title);
                AutomationProperties.SetHelpText(item, entry.Description);
                // 由页面构造成功后的Select提交选中态；失败时不提前改变菜单高亮。
                item.Click += (_, _) => RouteRequested?.Invoke(entry.RouteKey);
                category.Items.Add(item); destinations.Add(entry.RouteKey, (entry, item));
            }
            category.PreviewKeyDown += (_, e) =>
            {if(e.Key==Key.F4){OpenCategory(group.Key);e.Handled=true;}};
            categories.Add(group.Key,category);row.Items.Add(category);
        }
        Content = row;
        // 焦点与菜单模式由Menu/MenuItem共同管理，不在关闭回调中强制移动焦点。
        Unloaded += (_, _) => {foreach (var entry in categories.Values)entry.IsSubmenuOpen=false;};
    }

    public void OpenCategory(string key)
    {
        if (!categories.TryGetValue(key, out var current)) throw new ArgumentException("插件导航分类不存在。", nameof(key));
        foreach (var entry in categories.Values)if(!ReferenceEquals(entry,current))entry.IsSubmenuOpen=false;
        current.Focus();current.IsSubmenuOpen=true;
    }

    public void Select(string routeKey)
    {
        if (!destinations.TryGetValue(routeKey, out var current)) throw new ArgumentException("插件导航目的地不存在。", nameof(routeKey));
        SelectedRouteKey = routeKey;
        foreach (var entry in destinations.Values) entry.Item.IsChecked = entry.Destination.RouteKey == routeKey;
        foreach (var entry in categories) entry.Value.Tag = entry.Key == current.Destination.CategoryKey ? "Selected" : null;
    }
}
