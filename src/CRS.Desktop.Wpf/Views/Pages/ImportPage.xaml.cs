using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>绑定ImportPage页面模型，业务行为由应用用例负责。</summary>
public partial class ImportPage : Page
{
    public ImportPage(ShellViewModel shell) { InitializeComponent(); DataContext = shell.Import; }
    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e) => ((ImportViewModel)DataContext).Token = ((PasswordBox)sender).Password;
}
