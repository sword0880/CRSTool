using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>状态页只绑定宿主健康快照，不注册业务任务或访问存储。</summary>
public partial class WorkerStatusPage:UserControl
{
    public WorkerStatusPage(BackgroundServiceViewModel model){InitializeComponent();DataContext=model;}
}
