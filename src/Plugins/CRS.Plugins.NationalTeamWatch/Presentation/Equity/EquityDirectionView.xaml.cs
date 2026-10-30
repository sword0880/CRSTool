// 直接持股方向视图的交互绑定，分类及价格复核通过展示模型提交后台。
using System.Windows.Controls;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EquityDirectionView:UserControl
{
    public EquityDirectionView()=>InitializeComponent();
    private void OnOpenSource(object sender,System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if(e.Uri is {IsAbsoluteUri:true,Scheme:"https"} uri&&OfficialEquityDocuments.IsAllowed(uri))
        {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});e.Handled=true;}
    }
}
