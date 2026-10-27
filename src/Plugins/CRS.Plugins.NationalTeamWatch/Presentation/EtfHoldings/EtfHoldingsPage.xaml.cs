using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EtfHoldingsPage:UserControl
{
    public EtfHoldingsPage(EtfHoldingsViewModel model,bool loadOnStart=true)
    {
        InitializeComponent();DataContext=model;
        if(loadOnStart)Loaded+=async(_,_)=>await model.RefreshCommand.ExecuteAsync(null);
        Unloaded+=(_,_)=>model.Cancel();
    }
}
