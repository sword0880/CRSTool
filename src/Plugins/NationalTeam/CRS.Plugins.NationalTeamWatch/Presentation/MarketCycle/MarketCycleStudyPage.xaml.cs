// 市场周期研究页面初始化与模型绑定，研究信号的证据判断由后台完成。
using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
public partial class MarketCycleStudyPage:UserControl
{
    public MarketCycleStudyPage(MarketCycleStudyViewModel model){InitializeComponent();DataContext=model;model.Breadth.Changed+=model.InvalidateForBreadth;}
}
