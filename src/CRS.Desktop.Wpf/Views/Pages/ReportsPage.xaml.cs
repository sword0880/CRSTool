// 报表页面初始化并绑定宿主报表模型，文档生成与计算由对应应用服务提供。
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
public partial class ReportsPage : Page
{
    public ReportsPage(ShellViewModel shell) {InitializeComponent();DataContext=shell.Reports;}
}