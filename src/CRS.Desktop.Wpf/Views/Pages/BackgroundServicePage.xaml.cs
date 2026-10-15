using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>后台服务视图只绑定 DTO，不打开服务数据库或构建后台作业。</summary>
public partial class BackgroundServicePage : UserControl
{
    public BackgroundServicePage(BackgroundServiceViewModel model) { InitializeComponent(); DataContext = model; }
    // 只响应实际数据行，双击表头、滚动条或空白处不能误开上次选择的日志。
    private void OnHistoryDoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(HistoryGrid,origin) is DataGridRow {Item:ExecutionRecord record})OpenLog(record);
    }
    private void OnHistoryKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter && HistoryGrid.SelectedItem is ExecutionRecord record){e.Handled=true;OpenLog(record);}
    }
    private void OnViewLog(object sender,RoutedEventArgs e)
    {if(HistoryGrid.SelectedItem is ExecutionRecord record)OpenLog(record);}
    private void OpenLog(ExecutionRecord record)
    {
        var model=((BackgroundServiceViewModel)DataContext).CreateExecutionLog(record);
        if(model.Lifetime.IsCancellationRequested){model.Dispose();return;}
        new ExecutionLogWindow(model){Owner=Window.GetWindow(this)}.ShowDialog();
    }
}
