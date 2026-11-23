// 联合退出观察页面的展示入口，只装配研究模型，正式通知与投递由后台预警流程控制。
using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
public partial class JointExitStudyPage:UserControl
{
    public JointExitStudyPage(JointExitStudyViewModel model,bool loadOnStart=true)
    {InitializeComponent();DataContext=model;if(loadOnStart)Loaded+=async(_,_)=>await model.EnsureInitializedAsync();}
}
