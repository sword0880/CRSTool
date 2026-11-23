// 宿主顶部插件菜单的挂载与回收；切换页面时解除旧内容归属，挂载失败时恢复插件自身的展示状态。
using CRS.Plugin.Wpf;
using System.Windows;
namespace CRS.DesktopClient.Views;

public partial class MainWindow
{
    private IPluginShellHeader? hostedHeader;
    private void ClearPluginHeader()
    {
        PluginHeaderContent.Content=null;
        hostedHeader?.SetHeaderHosted(false);
        hostedHeader=null;
    }
    private void SetPluginHeader(UIElement page)
    {
        if(page is not IPluginShellHeader provider)return;
        provider.SetHeaderHosted(true);
        try{PluginHeaderContent.Content=provider.HeaderContent;hostedHeader=provider;}
        catch{provider.SetHeaderHosted(false);throw;}
    }
}
