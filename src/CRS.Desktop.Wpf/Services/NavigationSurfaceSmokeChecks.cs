using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CRS.DesktopClient.Views;
using CRS.DesktopClient.Views.Pages;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Services;

/// <summary>重现历史页、插件页与侧栏折叠的组合，验证实际可见内容互斥。</summary>
internal static class NavigationSurfaceSmokeChecks
{
    internal static async Task RunAsync(MainWindow window, Action<string> capture)
    {
        var navigation = (NavigationView)window.FindName("Navigation");
        var surface = (Border)window.FindName("PluginSurface");
        var host = (ContentControl)window.FindName("PluginContent");
        var header = (ContentControl)window.FindName("PluginHeaderContent");
        window.Navigate("history"); await Task.Delay(350);
        var builtIn = Find<NavigationViewContentPresenter>(navigation) ?? throw new InvalidOperationException("没有内置内容容器。");
        if (!builtIn.IsVisible || surface.IsVisible) throw new InvalidOperationException("内置页面未独占显示。");
        var extensions = (NavigationViewItem)window.FindName("ExtensionsGroup");
        var until = CRS.Timing.BeijingClock.Now.AddSeconds(15);
        while (!extensions.MenuItems.OfType<NavigationViewItem>().Any(item => Equals(item.Content, "插件管理")))
        { if (CRS.Timing.BeijingClock.Now > until) throw new TimeoutException("插件导航尚未初始化。"); await Task.Delay(100); }
        var pages = extensions.MenuItems.OfType<NavigationViewItem>().Where(item => item.Tag is string).ToArray();
        if (pages.Length < 2) throw new InvalidOperationException("导航回归需要发行包中的两个实际插件。");
        var index = 0;
        foreach (var item in pages)
        {
            item.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, item));
            await Task.Delay(350); var original = host.Content;
            if (original is null) throw new InvalidOperationException("实际插件页面未打开。");
            if(original is CRS.Plugin.Wpf.IPluginShellHeader provider && !ReferenceEquals(header.Content,provider.HeaderContent))
                throw new InvalidOperationException("插件顶栏未移交窗口，或出现重复菜单。");
            foreach (var expanded in new[] { false, true, false, true })
            {
                window.IsNavigationExpanded = expanded; await Task.Delay(350); window.UpdateLayout();
                var left = surface.TranslatePoint(new Point(), window).X - navigation.TranslatePoint(new Point(), window).X;
                if (!surface.IsVisible || builtIn.IsVisible || !ReferenceEquals(original, host.Content)
                    || Math.Abs(left - (expanded ? navigation.OpenPaneLength : navigation.CompactPaneLength)) > 1)
                    throw new InvalidOperationException("插件页面与历史页重叠，或侧栏切换后内容位置错误。");
                if (!expanded) capture($"plugin-{index}-collapsed");
            }
            window.Navigate("history"); await Task.Delay(350); window.UpdateLayout();
            if (!builtIn.IsVisible || surface.IsVisible || host.Content is not null || header.Content is not null) throw new InvalidOperationException("返回内置页面后仍有插件遮罩或顶栏。");
            index++;
        }
        var management = extensions.MenuItems.OfType<NavigationViewItem>().Single(item => Equals(item.Content, "插件管理"));
        Console.Error.WriteLine("smoke: 两个插件折叠/展开互斥通过，打开插件管理");
        management.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, management)); await Task.Delay(350); window.UpdateLayout();
        var page = host.Content as PluginManagementPage ?? throw new InvalidOperationException("插件表格未打开。");
        Console.Error.WriteLine($"smoke: management rows={page.Rows.Count}, versions={string.Join(',',page.Rows.Select(item=>item.Version))}, oldPageVisible={builtIn.IsVisible}");
        if (page.Rows.Count < 2 || page.Rows.Any(row => row.Version == "未确定") || builtIn.IsVisible)
            throw new InvalidOperationException("插件表格缺少实际版本或仍显示旧页面。");
        var row = page.Rows[0]; var state = row.StateLabel; var action = row.ActionLabel;
        // 保持选中行再截图，覆盖用户反馈的深色选中背景与文字不清晰场景。
        var table = (System.Windows.Controls.DataGrid)page.FindName("PluginTable");
        table.SelectedItem = row; table.Focus();
        row.ToggleCommand.Execute(null);
        if (row.StateLabel != state || row.ActionLabel == action || !row.NextLabel.Contains("重启后生效"))
            throw new InvalidOperationException("启停图标没有准确显示待生效状态。");
        window.UpdateLayout(); capture("plugin-management-pending");
        row.ToggleCommand.Execute(null); // 还原隔离冒烟目录的下次启动设置。
        window.UpdateLayout(); capture("plugin-management");
        var failed = new PluginManagementRow(new("fixture.failure", "保存失败夹具"), (_, _) => throw new IOException(), true);
        failed.ToggleCommand.Execute(null);
        if (!failed.NextLabel.Contains("保存失败") || failed.ActionLabel != "下次启动禁用") throw new InvalidOperationException("保存失败伪装成待生效成功。");
        window.IsNavigationExpanded = false; await Task.Delay(350); window.UpdateLayout();
        if (builtIn.IsVisible || !surface.IsVisible) throw new InvalidOperationException("管理页折叠侧栏后出现历史页。");
        capture("plugin-management-collapsed");
        window.IsNavigationExpanded = true; window.Navigate("history");
        Console.Error.WriteLine("smoke: 两个真实插件反复折叠/展开、内置页面互斥、管理表格版本及启停图标通过");
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (Find<T>(VisualTreeHelper.GetChild(parent, index)) is { } child) return child;
        return null;
    }
}
