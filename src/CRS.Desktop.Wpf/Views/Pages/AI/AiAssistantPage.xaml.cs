using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Pages.AI;

/// <summary>绑定公开 ETF 问答，重新进入页面时刷新当前默认模型。</summary>
public partial class AiAssistantPage : Page
{
    public AiAssistantPage(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell.AiAssistant;
        Loaded += (_, _) => shell.AiAssistant.RefreshModel();
    }
}
