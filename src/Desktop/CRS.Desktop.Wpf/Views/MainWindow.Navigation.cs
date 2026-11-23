using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Wpf.Ui.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace CRS.DesktopClient.Views;

public partial class MainWindow
{
    private ContextMenu? compactNavigationMenu;
    private NavigationViewItem? compactNavigationGroup;
    private NavigationViewItem? activeNavigationItem;

    private void InitializeCompactNavigation()
    {
        foreach(var item in NavigationEntries())
        {
            item.ToolTip ??= item.Content?.ToString();
            if(item.MenuItems.Count>0 || item==ExtensionsGroup)
            {
                // 截获折叠态的输入，避免控件自带弹出菜单与分类浮层同时打开。
                item.PreviewMouseLeftButtonDown+=(_,e)=>
                {
                    if(Navigation.IsPaneOpen || item.MenuItems.Count==0)return;
                    item.Focus();e.Handled=true;
                };
                item.PreviewMouseLeftButtonUp+=(_,e)=>
                {
                    if(Navigation.IsPaneOpen || item.MenuItems.Count==0)return;
                    ToggleCompactNavigation(item);e.Handled=true;
                };
                item.PreviewKeyDown+=(_,e)=>
                {
                    if(Navigation.IsPaneOpen || item.MenuItems.Count==0 || e.Key is not (Key.Enter or Key.Space or Key.Right))return;
                    ToggleCompactNavigation(item);e.Handled=true;
                };
            }
            else RegisterNavigationLeaf(item);
        }
        Navigation.PaneOpened+=(_,_)=>OnNavigationPaneChanged();
        Navigation.PaneClosed+=(_,_)=>OnNavigationPaneChanged();
        Deactivated+=(_,_)=>CloseCompactNavigation();
        Closed+=(_,_)=>CloseCompactNavigation();
    }

    private void RegisterNavigationLeaf(NavigationViewItem item)
    {
        item.Click+=(_,e)=>
        {
            if(!ReferenceEquals(e.Source,item) || forceClose)return;
            if(item.TargetPageType is null && PluginSurface.Visibility!=Visibility.Visible)return;
            CloseCompactNavigation();SetActiveNavigationItem(item);
        };
    }

    private void SetActiveNavigationItem(NavigationViewItem item)
    {
        activeNavigationItem=item;
        foreach(var entry in NavigationEntries())
            entry.IsActive=ReferenceEquals(entry,item) || !Navigation.IsPaneOpen && entry.MenuItems.Contains(item);
    }

    private void OnNavigationPaneChanged()
    {
        CloseCompactNavigation();
        if(activeNavigationItem is {} selected)
        {
            SetActiveNavigationItem(selected);
            foreach(var group in Navigation.MenuItems.OfType<NavigationViewItem>().Where(x=>x.MenuItems.Count>0))
                group.IsExpanded=Navigation.IsPaneOpen && group.MenuItems.Contains(selected);
        }
    }

    private void CloseCompactNavigation()
    {
        if(compactNavigationMenu is not {} menu)return;
        compactNavigationMenu=null;compactNavigationGroup=null;menu.IsOpen=false;
    }

    private void ToggleCompactNavigation(NavigationViewItem group)
    {
        if(compactNavigationMenu is {IsOpen:true} && ReferenceEquals(compactNavigationGroup,group))
        {CloseCompactNavigation();return;}
        CloseCompactNavigation();
        if(Navigation.IsPaneOpen || group.MenuItems.Count==0)return;
        var menu=new ContextMenu
        {
            Style=(Style)FindResource("CompactNavigationMenu"),
            Placement=PlacementMode.Right,PlacementTarget=group,HorizontalOffset=8,
            Tag=group.Content,MaxHeight=Math.Max(120,SystemParameters.WorkArea.Height-32)
        };
        foreach(var item in group.MenuItems.OfType<NavigationViewItem>())
        {
            var row=new MenuItem
            {
                Header=item.Content,Tag=item,IsChecked=ReferenceEquals(item,activeNavigationItem),
                Style=(Style)FindResource("CompactNavigationMenuItem")
            };
            row.SetBinding(IsEnabledProperty,new System.Windows.Data.Binding(nameof(IsEnabled)){Source=item});
            // 图标创建新实例，原菜单仍由 NavigationView 持有。
            if(item.Icon is SymbolIcon symbol)row.Icon=new SymbolIcon{Symbol=symbol.Symbol,FontSize=18};
            row.Click+=(_,_)=>InvokeCompactNavigationItem(item);
            menu.Items.Add(row);
        }
        compactNavigationMenu=menu;compactNavigationGroup=group;
        menu.Closed+=(_,_)=>
        {
            if(!ReferenceEquals(compactNavigationMenu,menu))return;
            compactNavigationMenu=null;compactNavigationGroup=null;
        };
        menu.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key!=Key.Escape)return;
            CloseCompactNavigation();group.Focus();e.Handled=true;
        };
        menu.Opened+=(_,_)=>
        {
            // 首次定位当前页面；其余行继续使用原生菜单的上下键和 Enter 导航。
            (menu.Items.OfType<MenuItem>().FirstOrDefault(x=>x.IsChecked&&x.IsEnabled)
                ?? menu.Items.OfType<MenuItem>().FirstOrDefault(x=>x.IsEnabled))?.Focus();
        };
        menu.IsOpen=true;
    }

    private void InvokeCompactNavigationItem(NavigationViewItem item)
    {
        CloseCompactNavigation();
        if(!item.IsEnabled || forceClose)return;
        if(item.TargetPageType is {} pageType)
        {
            HidePlugin();Navigation.Navigate(pageType);SetActiveNavigationItem(item);
        }
        else item.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,item));
    }
}
