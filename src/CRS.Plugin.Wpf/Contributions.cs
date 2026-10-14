using System.Windows;
namespace CRS.Plugin.Wpf;
/// <summary>可选界面贡献契约，与纯BCL插件能力分离。</summary>
public interface IPluginWpfSession { IReadOnlyList<PluginPageContribution> GetPages(); }
/// <summary>宿主给路由加插件前缀；页面工厂必须在WPF Dispatcher线程执行。</summary>
public sealed record PluginPageContribution(string RouteKey, string Title, Func<FrameworkElement> CreateView);
