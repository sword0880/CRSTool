using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>后台服务视图只绑定 DTO，不打开服务数据库或构建后台作业。</summary>
public partial class BackgroundServicePage : UserControl
{
    private readonly BackgroundServiceViewModel model;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(3) };

    public BackgroundServicePage(BackgroundServiceViewModel model)
    {
        this.model = model;
        InitializeComponent(); DataContext = model;
        TaskCenterTabs.SelectedItem = CurrentTasksTab;
        refresh.Tick += async (_, _) =>
        {
            if (TaskCenterTabs.SelectedItem == CurrentTasksTab) await model.RefreshTaskActivityAsync();
        };
        Loaded += (_, _) => ActivateSelectedTab();
        Unloaded+=(_,_)=>refresh.Stop();
        TaskCenterTabs.SelectionChanged+=(_,e)=>
        {
            if (e.Source == TaskCenterTabs) ActivateSelectedTab();
        };
    }
    public void ShowCurrentTasks() => SelectTab(CurrentTasksTab);
    public void ShowServicePlans() => SelectTab(ServicePlansTab);
    public void ShowRecentExecutions() => SelectTab(ExecutionHistoryTab);
    public void ShowServiceStatus() => SelectTab(ServiceStatusTab);

    private void SelectTab(TabItem tab)
    {
        if (TaskCenterTabs.SelectedItem == tab) ActivateSelectedTab();
        else TaskCenterTabs.SelectedItem = tab;
    }

    private void ActivateSelectedTab()
    {
        if (!IsLoaded) return;
        if (TaskCenterTabs.SelectedItem == CurrentTasksTab)
        {
            refresh.Start();
            _ = model.RefreshTaskActivityAsync();
            return;
        }

        refresh.Stop();
        if (TaskCenterTabs.SelectedItem == ServiceStatusTab)
            _ = model.RefreshServiceStatusCommand.ExecuteAsync(null);
        else if (TaskCenterTabs.SelectedItem == ServicePlansTab && model.Jobs.Count == 0 && !model.Busy)
            _ = model.RefreshCommand.ExecuteAsync(null);
        else if (TaskCenterTabs.SelectedItem == ExecutionHistoryTab)
            _ = model.LoadHistoryOptionsAsync();
    }
    private void OnPlanDoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(PlansGrid,origin) is DataGridRow {Item:ServicePlanItem plan})OpenPlan(plan);
    }
    private void OnPlanKeyDown(object sender,KeyEventArgs e)
    {if(e.Key==Key.Enter && PlansGrid.SelectedItem is ServicePlanItem plan){e.Handled=true;OpenPlan(plan);}}
    private void OpenPlan(ServicePlanItem plan)
    {
        var parent=(BackgroundServiceViewModel)DataContext;
        if(parent.Lifetime.IsCancellationRequested || parent.Busy)return;
        if(plan.IsMaintenance)new MaintenancePlanWindow(parent.Cleanup,parent.Lifetime){Owner=Window.GetWindow(this)}.ShowDialog();
        else new ScheduleEditWindow(new(parent,plan.Job!)){Owner=Window.GetWindow(this)}.ShowDialog();
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
