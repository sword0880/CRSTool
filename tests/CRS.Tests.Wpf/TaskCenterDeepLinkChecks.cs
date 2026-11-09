using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.DesktopClient.Views.Pages;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CRS.Maintenance;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

/// <summary>在隔离管道上验证主窗体任务中心入口、深链接和独立健康查询。</summary>
internal static class TaskCenterDeepLinkChecks
{
    public static void Run()
    {
        Exception? error=null;
        using var stop=new CancellationTokenSource();var control=new Control();
        var pipe="CRS.History.Manual."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        var thread=new Thread(() =>
        {
            var app = new Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            foreach (var name in new[] { "BrandPalette", "SharedStyles" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/CRS.Desktop.Wpf;component/Styles/{name}.xaml") });
            var window = new MainWindow(new ShellViewModel(new FakeUseCases(), new SilentInteraction(), workerClient: new(pipe)), startWorkspace: false)
                { Width = 1040, Height = 700, ShowActivated = false, ShowInTaskbar = false };
            window.Loaded += async (_, _) =>
            {
                try { await CheckWindowAsync(window, control); }
                catch (Exception failure) { error = failure; }
                finally { control.ReleasePlans.TrySetResult(); window.Close(); }
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();thread.Join();stop.Cancel();try{server.GetAwaiter().GetResult();}catch(OperationCanceledException){}
        if(error is not null)throw error;
    }

    private static async Task CheckWindowAsync(MainWindow window, Control control)
    {
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var entry = (Wpf.Ui.Controls.NavigationViewItem)window.FindName("TaskCenterItem");
        var group = (Wpf.Ui.Controls.NavigationViewItem)window.FindName("TasksGroup");
        var host = (ContentControl)window.FindName("PluginContent");
        var healthButton = (UIElement)window.FindName("FooterHealthButton");
        var issuesButton = (UIElement)window.FindName("FooterIssuesButton");
        var tasksButton = (UIElement)window.FindName("FooterTasksButton");
        if (group.MenuItems.Count != 1 || !ReferenceEquals(group.MenuItems[0], entry))
            throw new InvalidOperationException("系统管理仍保留重复的后台入口。");

        Click(healthButton);
        var page = host.Content as BackgroundServicePage ?? throw new InvalidOperationException("底栏服务状态未打开统一任务中心。");
        var model = (BackgroundServiceViewModel)page.DataContext;
        var tabs = (TabControl)page.FindName("TaskCenterTabs");
        string? SelectedTab() => (tabs.SelectedItem as TabItem)?.Header?.ToString();
        if (SelectedTab() != "服务状态" || !entry.IsActive || !group.IsActive ||
            !tabs.Items.OfType<TabItem>().Select(tab => tab.Header).SequenceEqual(new[] { "当前任务", "服务计划", "执行记录", "服务状态" }))
            throw new InvalidOperationException("任务中心页签顺序、底栏直达或导航高亮不符。");
        await WaitForAsync(() => model.HealthTone == "Success");
        if (control.ActivityReads != 0 || control.PlanReads != 0 || control.HistoryReads != 0)
            throw new InvalidOperationException("单独打开服务状态读取了任务数据。");

        Click(entry);
        if (!ReferenceEquals(host.Content, page) || SelectedTab() != "当前任务" ||
            page.FindName("RunningTasksGrid") is not DataGrid || page.FindName("WaitingTasksGrid") is not DataGrid || page.FindName("PendingTasksGrid") is not DataGrid)
            throw new InvalidOperationException("菜单进入任务中心未默认打开当前任务及三张表。");
        page.ShowServicePlans();
        await control.PlansRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        control.HealthState = "StorageFault";
        Click(healthButton);
        await WaitForAsync(() => model.HealthTone == "Error");
        if (SelectedTab() != "服务状态" || !model.Busy || control.PlanReads != 1)
            throw new InvalidOperationException("计划读取未完成时，服务状态无法独立查询。");
        control.ReleasePlans.TrySetResult();
        await WaitForAsync(() => !model.Busy);
        if (model.HealthTone != "Error") throw new InvalidOperationException("计划读取失败覆盖了已经确认的服务状态。");

        control.HealthState = "Unavailable";
        Click(healthButton);
        await WaitForAsync(() => model.HealthLabel == "无法确认服务状态");
        if (model.HealthTone != "Warning" || model.SnapshotTime != "本次未取得健康快照")
            throw new InvalidOperationException("连接失败后仍显示旧服务采样。");

        Click(tasksButton);
        if (SelectedTab() != "当前任务") throw new InvalidOperationException("底栏任务数没有直达当前任务。");
        Click(issuesButton);
        window.UpdateLayout();
        if (SelectedTab() != "执行记录" || !model.OnlyIssues || !ReferenceEquals(host.Content, page))
            throw new InvalidOperationException("底栏异常提示未直达执行记录和异常筛选。");

        var plans = (DataGrid)page.FindName("PlansGrid");
        if (!plans.Columns.OfType<DataGridTextColumn>().Any(x => x.Header?.ToString() == "任务编号（英文）" && x.Binding is System.Windows.Data.Binding { Path.Path: "JobId" }))
            throw new InvalidOperationException("服务计划缺少英文任务编号。");
        if (page.FindName("HistoryStartDatePicker") is not DatePicker { SelectedDateFormat: DatePickerFormat.Short } ||
            page.FindName("HistoryEndDatePicker") is not DatePicker { SelectedDateFormat: DatePickerFormat.Short } || page.FindName("MoreRecordsButton") is not null)
            throw new InvalidOperationException("执行记录缺少起止日期查询入口。");
        if (Find<Button>(page).Any(x => x.Content?.ToString() == "今天")) throw new InvalidOperationException("执行记录仍显示今天按钮。");
        var check = page.FindName("OnlyIssuesCheckBox") as CheckBox
            ?? throw new InvalidOperationException("执行记录缺少异常筛选控件。");
        if (check.IsChecked != true) throw new InvalidOperationException("异常筛选未绑定到任务模型。");
        model.OnlyIssues = false;
        window.UpdateLayout();
        if (check.IsChecked != false) throw new InvalidOperationException("恢复全部记录未清除勾选。");
        await WaitForAsync(() => model.HistoryJobs.Contains("manual"));
        var pluginFilter = (ComboBox)page.FindName("HistoryPluginFilter");
        var jobFilter = (ComboBox)page.FindName("HistoryJobFilter");
        jobFilter.SetCurrentValue(Selector.SelectedItemProperty, DataCleanupTask.JobId);
        pluginFilter.SetCurrentValue(Selector.SelectedItemProperty, "plugin.one");
        if (model.HistoryJob != "" || jobFilter.Text != "" || !model.HistoryJobs.SequenceEqual(new[] { "", "one", "shared" }))
            throw new InvalidOperationException("插件切换未过滤任务或未清掉不匹配的任务。");
        jobFilter.SetCurrentValue(Selector.SelectedItemProperty, "shared");
        pluginFilter.SetCurrentValue(Selector.SelectedItemProperty, "plugin.two");
        if (model.HistoryJob != "shared" || !model.HistoryJobs.ToHashSet().SetEquals(new[] { "", "shared", "manual" }))
            throw new InvalidOperationException("插件切换清掉了仍然有效的任务，或遗漏后续页的任务。");
        pluginFilter.SetCurrentValue(Selector.SelectedItemProperty, DataCleanupTask.ServiceId);
        if (model.HistoryJob != "" || !model.HistoryJobs.SequenceEqual(new[] { "", DataCleanupTask.JobId }))
            throw new InvalidOperationException("后台服务只应列出资料清理任务。");
        pluginFilter.SetCurrentValue(Selector.SelectedItemProperty, "");
        if (model.HistoryPlugin != "" || !model.HistoryJobs.ToHashSet().SetEquals(new[] { "", DataCleanupTask.JobId, "one", "shared", "manual" }))
            throw new InvalidOperationException("清空插件后未恢复全部去重后的任务。");
        await Task.Delay(3500);
        if (control.HistoryReads != 0) throw new InvalidOperationException("进入执行记录或等待3秒后仍自动查询。");
        foreach (var status in new[] { "RetryDeferred", "Failed", "AccessBlocked", "SourceSchemaChanged", "Interrupted", "Rejected" })
            if (!BackgroundServiceViewModel.IsIssueResult(status)) throw new InvalidOperationException("异常状态丢失 " + status);
        foreach (var status in new[] { "Succeeded", "NotPublished", "Running", "Cancelled" })
            if (BackgroundServiceViewModel.IsIssueResult(status)) throw new InvalidOperationException("普通或主动取消状态误报为异常 " + status);
        Console.WriteLine("PASS 任务中心：插件与任务下拉联动、跨页任务、后台清理、历史手动查询和原有导航。");
    }

    private static void Click(UIElement element) => element.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, element));

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!predicate() && Environment.TickCount64 < deadline) await Task.Delay(50);
        if (!predicate()) throw new TimeoutException("任务中心未在测试时限内更新状态。");
    }

    private sealed class Control:IBackgroundControl
    {
        internal int HistoryReads, ActivityReads, PlanReads;
        internal volatile string HealthState = "Running";
        internal readonly TaskCompletionSource PlansRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleasePlans = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if (request.Command == "GetHealth")
            {
                var state = HealthState;
                return state == "Unavailable" ? new(WorkerProtocol.Version, request.RequestId, "Unavailable")
                    : new(WorkerProtocol.Version, request.RequestId, "Ok", Health: new(state, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1));
            }
            if (request.Command == "GetJobs")
            {
                Interlocked.Increment(ref PlanReads);
                PlansRequested.TrySetResult();
                await ReleasePlans.Task.WaitAsync(token);
                return new(WorkerProtocol.Version, request.RequestId, "Unavailable");
            }
            if (request.Command == "GetHistoryJobs")
            {
                var first = request.Cursor is null;
                TaskDefinition Job(string plugin, string job) => new(plugin, job, job, "0 * * * * ?", "China Standard Time", true, 1, false);
                return new(WorkerProtocol.Version, request.RequestId, "Ok", Jobs: first
                    ? [Job("plugin.one", "one"), Job("plugin.one", "shared")]
                    : [Job("plugin.two", "manual"), Job("plugin.two", "shared")], NextCursor: first ? "page-two" : null);
            }
            if (request.Command == "GetTaskActivity")
            {
                Interlocked.Increment(ref ActivityReads);
                return new(WorkerProtocol.Version, request.RequestId, "Ok", TaskActivity: new(CRS.Timing.BeijingClock.Now, [], 0, 0, 0));
            }
            if (request.Command == "GetHistoryByDate") Interlocked.Increment(ref HistoryReads);
            return new(WorkerProtocol.Version, request.RequestId, "UnknownCommand");
        }
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);
            if(child is T match)yield return match;
            foreach(var descendant in Find<T>(child))yield return descendant;
        }
    }
}
