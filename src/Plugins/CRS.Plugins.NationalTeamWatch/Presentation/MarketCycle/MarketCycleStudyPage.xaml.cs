using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
public partial class MarketCycleStudyPage:UserControl
{
    public MarketCycleStudyPage(MarketCycleStudyViewModel model){InitializeComponent();DataContext=model;model.Breadth.Changed+=model.InvalidateForBreadth;}
}
