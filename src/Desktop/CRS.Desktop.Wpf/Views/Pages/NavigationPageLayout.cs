using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views.Pages;

/// <summary>页面加载后使用导航的可见高度，确保内部表格和表单独立滚动。</summary>
internal static class NavigationPageLayout
{
    internal static void Attach(Page page)
    {
        page.Loaded += OnLoaded;
        page.Unloaded += OnUnloaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var page = (Page)sender;
        ScrollContentPresenter? viewport = null;
        // 创建及设计预览时页面尚无导航祖先，不能在XAML中立即查找。
        // 内容呈现器会随长表单撑高，必须使用外层滚动视口的实际高度。
        for (var parent = VisualTreeHelper.GetParent(page); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            viewport ??= parent as ScrollContentPresenter;
            if (parent is not NavigationView) continue;
            if (viewport is not null)
                page.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(FrameworkElement.ActualHeight))
                {
                    Source = viewport,
                    Mode = BindingMode.OneWay
                });
            return;
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 缓存页面离开导航后释放宿主引用，重新加载时绑定新的可见区域。
        BindingOperations.ClearBinding((Page)sender, FrameworkElement.HeightProperty);
    }
}
