using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>绑定ReportsPage页面模型，业务行为由应用用例负责。</summary>
public partial class ReportsPage : Page
{
    private readonly ReportsViewModel model;
    public ReportsPage(ShellViewModel shell)
    {
        model=shell.Reports; InitializeComponent(); DataContext=model;
        Loaded+=(_,_)=>model.SensitiveCleared+=Clear;
        Unloaded+=(_,_)=> {model.ClearSensitiveInputs(); Clear(); model.SensitiveCleared-=Clear;};
    }
    // PasswordBox 不写普通文本绑定，生命周期结束时同时清理控件与模型。
    private void PasswordChanged(object sender,System.Windows.RoutedEventArgs e)=>model.SetPassword(((PasswordBox)sender).Password);
    private void ConfirmationChanged(object sender,System.Windows.RoutedEventArgs e)=>model.SetConfirmation(((PasswordBox)sender).Password);
    private void Clear() {ExportSecret.Clear(); ConfirmSecret.Clear();}
}
