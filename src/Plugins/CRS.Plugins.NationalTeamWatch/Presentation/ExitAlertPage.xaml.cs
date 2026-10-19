using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    /// <summary>表格或详情读到边界后滚动整页，避免鼠标停在记录区就无法查看下方证据。</summary>
    private void OnTableMouseWheel(object sender,MouseWheelEventArgs e)
    {
        if(sender is not DependencyObject element||FindScroll(element) is not {} inner)return;
        if(!(e.Delta>0&&inner.VerticalOffset<=0||e.Delta<0&&inner.VerticalOffset>=inner.ScrollableHeight))return;
        for(var parent=VisualTreeHelper.GetParent(element);parent is not null;parent=VisualTreeHelper.GetParent(parent))
            if(parent is ScrollViewer outer){outer.ScrollToVerticalOffset(outer.VerticalOffset-e.Delta);e.Handled=true;return;}
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if(parent is ScrollViewer scroll)return scroll;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)if(FindScroll(VisualTreeHelper.GetChild(parent,i)) is {} child)return child;
        return null;
    }
}
