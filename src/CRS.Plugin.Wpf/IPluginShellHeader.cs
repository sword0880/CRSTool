using System.Windows;
namespace CRS.Plugin.Wpf;

/// <summary>可移交给宿主顶栏的插件菜单。宿主先清空原容器再切换承载状态；独立打开时由页面自行承载。</summary>
public interface IPluginShellHeader
{
    FrameworkElement HeaderContent { get; }
    void SetHeaderHosted(bool hosted);
}
