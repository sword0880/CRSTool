using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>后台服务视图只绑定 DTO，不打开服务数据库或构建后台作业。</summary>
public partial class BackgroundServicePage : UserControl
{
    public BackgroundServicePage(BackgroundServiceViewModel model) { InitializeComponent(); DataContext = model; }
}
