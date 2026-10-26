using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
/// <summary>打开页面才查询已登记资料，默认不启动任何源站采集。</summary>
public partial class IndexResearchPage:UserControl
{
    public IndexResearchPage(IndexResearchViewModel model,bool loadOnStart=true){InitializeComponent();DataContext=model;if(loadOnStart)Loaded+=async(_,_)=>await model.EnsureInitializedAsync();}
}
