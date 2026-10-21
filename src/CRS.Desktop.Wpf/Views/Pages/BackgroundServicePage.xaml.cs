using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>后台服务视图只绑定 DTO，不打开服务数据库或构建后台作业。</summary>
public partial class BackgroundServicePage : UserControl
{
    public BackgroundServicePage(BackgroundServiceViewModel model)
    {
        InitializeComponent(); DataContext = model;
        var refresh=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
        async Task RefreshVisibleAsync()
        {if(TaskCenterTabs.SelectedIndex==2)await model.RefreshTaskActivityAsync();else if(TaskCenterTabs.SelectedIndex==1)await model.RefreshRecentHistoryAsync();}
        refresh.Tick+=async (_,_)=>await RefreshVisibleAsync();
        Loaded+=(_,_)=>{if(TaskCenterTabs.SelectedIndex is 1 or 2){refresh.Start();_ = RefreshVisibleAsync();}};
        Unloaded+=(_,_)=>refresh.Stop();
        TaskCenterTabs.SelectionChanged+=(_,e)=>
        {
            if(e.Source!=TaskCenterTabs)return;
            if(IsLoaded && TaskCenterTabs.SelectedIndex is 1 or 2){refresh.Start();_ = RefreshVisibleAsync();}
            else refresh.Stop();
        };
    }
    /// <summary>主窗体底栏直达“最近执行”，不再默认停留在“服务计划”。</summary>
    public void ShowRecentExecutions() => TaskCenterTabs.SelectedIndex=1;
    public void ShowCurrentTasks()=>TaskCenterTabs.SelectedIndex=2;
    private void OnPlanDoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(PlansGrid,origin) is DataGridRow {Item:TaskDefinition plan})OpenPlan(plan);
    }
    private void OnPlanKeyDown(object sender,KeyEventArgs e)
    {if(e.Key==Key.Enter && PlansGrid.SelectedItem is TaskDefinition plan){e.Handled=true;OpenPlan(plan);}}
    private void OpenPlan(TaskDefinition plan)
    {
        var parent=(BackgroundServiceViewModel)DataContext;
        if(parent.Lifetime.IsCancellationRequested || parent.Busy)return;
        new ScheduleEditWindow(new(parent,plan)){Owner=Window.GetWindow(this)}.ShowDialog();
    }
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
