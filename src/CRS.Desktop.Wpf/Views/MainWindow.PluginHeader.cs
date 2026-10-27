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
