using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>绑定ReportsPage页面模型，业务行为由应用用例负责。</summary>
public partial class ReportsPage : Page
{
    public ReportsPage(ShellViewModel shell) { InitializeComponent(); DataContext = shell.Reports; }
}
