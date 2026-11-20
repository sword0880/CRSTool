using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
/// <summary>行业研究视图；组合入口必须显式提供后端报告和来源说明，界面不伪造默认行情。</summary>
public partial class SectorResearchPage:UserControl
{
    public SectorResearchPage(SectorResearchViewModel model){InitializeComponent();DataContext=model;}
}
