using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EquityPage:UserControl
{
    public EquityPage(EquityViewModel viewModel, bool loadOnStart=true)
    {InitializeComponent();DataContext=viewModel;if(loadOnStart)Loaded+=async(_,_)=>await viewModel.RefreshCommand.ExecuteAsync(null);}
    private void OnOpenSource(object sender,System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if(e.Uri is {IsAbsoluteUri:true,Scheme:"https"} uri && uri.UserInfo.Length==0 && uri.IsDefaultPort
            && (uri.Host is "www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "static.sse.com.cn" or "www.szse.cn" or "disc.static.szse.cn"
                ||CRS.Plugins.NationalTeamWatch.Domain.OfficialEquityDocuments.IsReviewedIssuer(uri)))
        {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});e.Handled=true;}
    }
}
