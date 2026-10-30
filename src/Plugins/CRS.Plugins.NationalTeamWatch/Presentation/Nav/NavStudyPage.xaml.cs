// NAV研究页面的展示装配入口，绑定已有展示模型，金额计算和资料核验由后台研究用例完成。
using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class NavStudyPage:UserControl
{
    public NavStudyPage(NavStudyViewModel model,bool loadOnStart=true)
    {InitializeComponent();DataContext=model;if(loadOnStart)Loaded+=async(_,_)=>await model.EnsureInitializedAsync();}
}
