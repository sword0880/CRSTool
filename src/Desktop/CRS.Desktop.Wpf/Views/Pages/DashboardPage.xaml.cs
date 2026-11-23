using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>绑定DashboardPage页面模型，业务行为由应用用例负责。</summary>
public partial class DashboardPage : Page
{
    private readonly DashboardViewModel model;
    private CancellationTokenSource? accountRead;

    public DashboardPage(ShellViewModel shell)
    {
        model = shell.Dashboard;
        InitializeComponent();
        DataContext = model;
        Loaded += async (_, _) =>
        {
            shell.History.RecentTasksRefreshed += OnRecentTasksRefreshed;
            using var request = new CancellationTokenSource();
            accountRead = request;
            try { await Task.WhenAll(model.RefreshAsync(), shell.Performance.RefreshAccountBrokerSummaryAsync(request.Token)); }
            finally { if (accountRead == request) accountRead = null; }
        };
        Unloaded += (_, _) =>
        {
            shell.History.RecentTasksRefreshed -= OnRecentTasksRefreshed;
            accountRead?.Cancel();
        };
    }

    private async void OnRecentTasksRefreshed() => await model.RefreshAsync();

    private async void OnYearSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (YearSelector.SelectedItem is int year) await model.SelectYearAsync(year);
    }
}
