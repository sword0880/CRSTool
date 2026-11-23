using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>绑定TradesPage页面模型，业务行为由应用用例负责。</summary>
public partial class TradesPage : Page
{
    public TradesPage(ShellViewModel shell) { InitializeComponent(); DataContext = shell.Results; }
}
