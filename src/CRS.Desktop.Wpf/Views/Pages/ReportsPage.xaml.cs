using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
public partial class ReportsPage : Page
{
    public ReportsPage(ShellViewModel shell) {InitializeComponent();DataContext=shell.Reports;}
}