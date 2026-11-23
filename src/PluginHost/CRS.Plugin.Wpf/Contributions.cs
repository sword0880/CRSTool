using System.Windows;
namespace CRS.Plugin.Wpf;
/// <summary>可选界面贡献契约，与纯BCL插件能力分离。</summary>
public interface IPluginWpfSession { IReadOnlyList<PluginPageContribution> GetPages(); }
/// <summary>宿主给路由加插件前缀；页面工厂必须在WPF Dispatcher线程执行。</summary>
public sealed record PluginPageContribution(string RouteKey, string Title, Func<FrameworkElement> CreateView)
{
    /// <summary>兼容旧插件：默认显示在主菜单；复合工作台内的旧直达路由可仅保持注册而不重复占据侧栏。</summary>
    public bool ShowInNavigation {get;init;}=true;
}
