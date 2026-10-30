// 具名ETF持仓历史视图，只绑定历史研究展示模型，跨期比较与缺口判断由后台负责。
using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
public partial class EtfHoldingHistoryView:UserControl
{
    public EtfHoldingHistoryView(){InitializeComponent();}
}
