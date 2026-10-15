using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>计划任务视图只接收展示模型，不直接操作Quartz或文件。</summary>
public partial class ScheduledTasksPage:UserControl
{
    public ScheduledTasksPage(ScheduledTasksViewModel model){InitializeComponent();DataContext=model;}
}
