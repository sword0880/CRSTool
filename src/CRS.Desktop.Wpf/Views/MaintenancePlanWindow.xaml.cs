using CRS.DesktopClient.ViewModels;
using Wpf.Ui.Controls;
namespace CRS.DesktopClient.Views;
public partial class MaintenancePlanWindow:FluentWindow
{
    public MaintenancePlanWindow(DataCleanupViewModel? model,CancellationToken lifetime)
    {
        InitializeComponent();DataContext=model;
        var registration=lifetime.Register(()=>Dispatcher.BeginInvoke(new Action(()=>{if(IsLoaded)Close();})));
        Closed+=(_,_)=>registration.Dispose();
        Loaded+=(_,_)=>{if(lifetime.IsCancellationRequested)Close();};
    }
    private void OnClose(object sender,System.Windows.RoutedEventArgs e)=>Close();
}
