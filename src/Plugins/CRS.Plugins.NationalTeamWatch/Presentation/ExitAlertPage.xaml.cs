using System.Windows.Controls;
using System.Windows;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>只绑定视图及打开经白名单核验的官方HTTPS原件。</summary>
public partial class ExitAlertPage:UserControl
{
    public ExitAlertPage()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>{if(DataContext is ExitAlertViewModel model)await model.RefreshCommand.ExecuteAsync(null);};
    }
    private void OnOpenSource(object sender,System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if(e.Uri is {IsAbsoluteUri:true,Scheme:"https",IsDefaultPort:true,UserInfo:""} uri
            &&(Domain.OfficialEquityDocuments.IsAllowed(uri)&&!Domain.OfficialEquityDocuments.IsReviewedIssuer(uri)
                ||uri.Host is "query.sse.com.cn" or "www.sse.com.cn" or "www.szse.cn"))
        {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});e.Handled=true;}
    }
}
