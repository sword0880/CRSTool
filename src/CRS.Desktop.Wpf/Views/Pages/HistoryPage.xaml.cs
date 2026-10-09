using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>绑定HistoryPage页面模型，业务行为由应用用例负责。</summary>
public partial class HistoryPage : Page
{
    public HistoryPage(ShellViewModel shell) { InitializeComponent(); DataContext = shell.History; }
}
