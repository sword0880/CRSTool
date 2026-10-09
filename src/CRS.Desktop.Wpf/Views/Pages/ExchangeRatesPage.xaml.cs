using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>绑定ExchangeRatesPage页面模型，业务行为由应用用例负责。</summary>
public partial class ExchangeRatesPage : Page
{
    public ExchangeRatesPage(ShellViewModel shell) { InitializeComponent(); DataContext = shell.ExchangeRates; }
}
