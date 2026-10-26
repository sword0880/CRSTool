using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CRS.Plugin.Wpf;

public sealed record PluginNavigationCategory(string Key, string Title);
public sealed record PluginNavigationDestination(string RouteKey, string CategoryKey, string Title, string Description);

/// <summary>跨插件共用的顶部分类菜单；只提出导航请求，不创建业务页面、不查询或保存数据。</summary>
public sealed class PluginSectionNavigation : UserControl
{
    private readonly Dictionary<string, (Button Button, ContextMenu Menu)> categories = new(StringComparer.Ordinal);
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
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var group in groups)
        {
            var members = entries.Where(x => x.CategoryKey == group.Key).ToArray();
            if (members.Length == 0) continue;
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = group.Title, VerticalAlignment = VerticalAlignment.Center });
            var chevron = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M 0,0 L 4,4 L 8,0"),
                StrokeThickness = 1.5, Width = 8, Height = 5, Margin = new Thickness(10, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "BrandTextSecondaryBrush"); label.Children.Add(chevron);
            var button = new Button { Content = label, Style = (Style)Resources["PluginSectionButton"], Margin = new Thickness(0, 0, 4, 0) };
            AutomationProperties.SetName(button, group.Title + "菜单");
            AutomationProperties.SetHelpText(button, "按Enter、向下键或F4打开功能菜单。");
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, VerticalOffset = 5,
                Style = (Style)Resources["PluginSectionMenu"] };
            button.ContextMenu = menu;
            foreach (var entry in members)
            {
                var item = new MenuItem { Header = entry.Title, ToolTip = entry.Description,
                    Style = (Style)Resources["PluginDestinationItem"] };
                AutomationProperties.SetName(item, entry.Title);
                AutomationProperties.SetHelpText(item, entry.Description);
                // 由页面构造成功后的Select提交选中态；失败时不提前改变菜单高亮。
                item.Click += (_, _) => RouteRequested?.Invoke(entry.RouteKey);
                menu.Items.Add(item); destinations.Add(entry.RouteKey, (entry, item));
            }
            button.Click += (_, _) => OpenCategory(group.Key);
            button.PreviewKeyDown += (_, e) =>
            { if (e.Key is Key.Down or Key.F4) { OpenCategory(group.Key); e.Handled = true; } };
            menu.Opened += (_, _) =>
                (menu.Items.Cast<MenuItem>().FirstOrDefault(x => x.IsChecked) ?? (MenuItem)menu.Items[0]).Focus();
            menu.Closed += (_, _) => { if (button.IsVisible) button.Focus(); };
            categories.Add(group.Key, (button, menu)); row.Children.Add(button);
        }
        Content = row;
        Unloaded += (_, _) => { foreach (var entry in categories.Values) entry.Menu.IsOpen = false; };
    }

    public void OpenCategory(string key)
    {
        if (!categories.TryGetValue(key, out var current)) throw new ArgumentException("插件导航分类不存在。", nameof(key));
        foreach (var entry in categories.Values) entry.Menu.IsOpen = false;
        current.Menu.IsOpen = true;
    }

    public void Select(string routeKey)
    {
        if (!destinations.TryGetValue(routeKey, out var current)) throw new ArgumentException("插件导航目的地不存在。", nameof(routeKey));
        SelectedRouteKey = routeKey;
        foreach (var entry in destinations.Values) entry.Item.IsChecked = entry.Destination.RouteKey == routeKey;
        foreach (var entry in categories) entry.Value.Button.Tag = entry.Key == current.Destination.CategoryKey ? "Selected" : null;
    }
}
