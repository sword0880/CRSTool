// 机构直接持股页面的展示装配，界面命令由展示模型管理，股票披露与方向计算由后台负责。
using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EquityPage:UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer refreshTimer=new(){Interval=TimeSpan.FromMinutes(1)};
    private bool refreshing;
    public EquityPage(EquityViewModel viewModel, bool loadOnStart=true)
    {
        InitializeComponent();DataContext=viewModel;
        if(loadOnStart)
        {
            refreshTimer.Tick+=async(_,_)=>await RefreshVisible(viewModel);
            Loaded+=async(_,_)=>{refreshTimer.Start();await RefreshVisible(viewModel);};
            Unloaded+=(_,_)=>refreshTimer.Stop();
        }
    }
    // 页面可见时只读刷新；审核表单打开或后台交互尚未结束时不覆盖用户正在核对的内容。
    private async Task RefreshVisible(EquityViewModel model)
    {
        if(refreshing||model.Busy||model.AdvancedTools)return;
        refreshing=true;try{await model.RefreshCommand.ExecuteAsync(null);}finally{refreshing=false;}
    }
    private void OnOpenSource(object sender,System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if(e.Uri is {IsAbsoluteUri:true,Scheme:"https"} uri && uri.UserInfo.Length==0 && uri.IsDefaultPort
            && (uri.Host is "www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "static.sse.com.cn" or "www.szse.cn" or "disc.static.szse.cn"
                ||CRS.Plugins.NationalTeamWatch.Domain.OfficialEquityDocuments.IsReviewedIssuer(uri)))
        {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});e.Handled=true;}
    }
}
