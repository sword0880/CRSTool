using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>绑定DashboardPage页面模型，业务行为由应用用例负责。</summary>
public partial class DashboardPage : Page
{
    private CancellationTokenSource? accountRead;

    public DashboardPage(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell.Dashboard;
        Loaded += async (_, _) =>
        {
            using var request = new CancellationTokenSource();
            accountRead = request;
            try { await shell.Performance.RefreshAccountBrokerSummaryAsync(request.Token); }
            finally { if (accountRead == request) accountRead = null; }
        };
        Unloaded += (_, _) => accountRead?.Cancel();
    }
}
