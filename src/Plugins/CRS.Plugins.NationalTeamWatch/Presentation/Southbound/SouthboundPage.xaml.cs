using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class SouthboundPage:UserControl
{
    public SouthboundPage(SouthboundViewModel model,bool loadOnStart=true)
    {
        InitializeComponent();DataContext=model;
        if(loadOnStart)Loaded+=async(_,_)=>await model.EnsureInitializedAsync();
    }
}
