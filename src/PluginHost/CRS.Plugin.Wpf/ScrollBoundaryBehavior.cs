using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CRS.Plugin.Wpf;

/// <summary>
/// 表格/多行输入框抵达自身滚动边界时，把滚轮交还给最近的父滚动区。
/// 只负责视觉输入，不执行页面命令；插件和宿主均可重用。
/// </summary>
public static class ScrollBoundaryBehavior
{
    public static readonly DependencyProperty ForwardAtEdgeProperty =
        DependencyProperty.RegisterAttached(
            "ForwardAtEdge", typeof(bool), typeof(ScrollBoundaryBehavior),
            new PropertyMetadata(false, OnForwardAtEdgeChanged));

    public static bool GetForwardAtEdge(DependencyObject element) => (bool)element.GetValue(ForwardAtEdgeProperty);
    public static void SetForwardAtEdge(DependencyObject element, bool value) => element.SetValue(ForwardAtEdgeProperty, value);

    private static void OnForwardAtEdgeChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement target) return;
        if ((bool)args.OldValue) target.PreviewMouseWheel -= ForwardWheel;
        if ((bool)args.NewValue) target.PreviewMouseWheel += ForwardWheel;
    }

    private static void ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject target) return;
        var inner = FindChildScroll(target);
        var outer = FindParentScroll(target);
        if (inner is null || outer is null || outer.ScrollableHeight <= 0) return;
        if (e.Delta > 0 && inner.VerticalOffset > 0 ||
            e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight) return;
        outer.ScrollToVerticalOffset(Math.Clamp(outer.VerticalOffset - e.Delta, 0, outer.ScrollableHeight));
        e.Handled = true;
    }

    private static ScrollViewer? FindParentScroll(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        return null;
    }

    private static ScrollViewer? FindChildScroll(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            if (FindChildScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
