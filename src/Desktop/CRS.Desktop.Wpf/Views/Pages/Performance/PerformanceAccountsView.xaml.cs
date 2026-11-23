using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Pages.Performance;

public partial class PerformanceAccountsView : UserControl
{
    public PerformanceAccountsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is false && DataContext is PerformanceViewModel model)
                model.ShowLatestAssetAmount = false;
        };
    }
}
