using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
/// <summary>页面只绑定模型，首次打开才查询后台；不阻塞插件启动或自行发起采集。</summary>
public partial class IndustryResearchPage:UserControl
{
    public IndustryResearchPage(IndustryResearchViewModel model,bool loadOnStart=true)
    {InitializeComponent();DataContext=model;if(loadOnStart)Loaded+=async(_,_)=>await model.EnsureInitializedAsync();}
}
