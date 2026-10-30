// 机构ETF持仓页面的装配与按需加载，页面离开时释放等待，目录、原件和已批准持仓分别展示。
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
