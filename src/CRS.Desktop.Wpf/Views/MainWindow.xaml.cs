using System.ComponentModel;
using CRS.DesktopClient.Services;
using CRS.Plugin.Wpf;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages;
using Wpf.Ui.Controls;
namespace CRS.DesktopClient.Views;
/// <summary>导航壳只管理页面缓存和窗口生命周期。</summary>
public partial class MainWindow : FluentWindow
{
    private bool forceClose;
    private CRS.PluginHost.PluginRuntime? plugins;
    private readonly CancellationTokenSource taskPageLifetime=new();
    private BackgroundServiceViewModel? backgroundServiceModel;
    private Task? pluginStartup;
    private System.Windows.UIElement? builtInContent;
    private readonly List<NavigationViewItem> pluginItems = [];
    private static readonly Dictionary<string,IReadOnlySet<string>> startupEnablement=[];
    public event Action? SecurityRequested;
    public event Action? LockRequested;
    private void OnLock(object sender,System.Windows.RoutedEventArgs e)=>LockRequested?.Invoke();
    /// <summary>安全锁定独立于普通关闭，取消请求、清空令牌并解除敏感页面引用。</summary>
    public void PrepareLock()
    {
        forceClose=true;
        if(DataContext is ShellViewModel activeShell)activeShell.StatusBar.Stop();
        taskPageLifetime.Cancel();
        plugins?.Cancel(); HidePlugin();
        // 换手机限制会话与系统锁屏可能先后触发，已清理窗口应允许重复关闭。
        if(DataContext is not ShellViewModel shell) return;
        foreach(var settings in OwnedWindows.OfType<SettingsWindow>().ToArray()) settings.AbortForLock();
        shell.Settings?.ClearSecurityInputs();
        shell.Reports.ClearSensitiveInputs(); // 锁定也清空导出密码，不能随页面缓存继续保留。
        shell.Performance.ClearForLock(); // 绩效与税务同样属于敏感会话，锁定时取消并清理。
        shell.State.UseCases.EndSession(); shell.Import.CancelCommand.Execute(null); shell.History.CancelOperationCommand.Execute(null);
        shell.Import.Token=""; shell.State.Clear(); DataContext=null;
    }
    public static readonly IReadOnlyDictionary<string, Type> PageTypes = new Dictionary<string, Type>
    {
        ["dashboard"] = typeof(DashboardPage), ["import"] = typeof(ImportPage), ["trades"] = typeof(TradesPage),
        ["fifo"] = typeof(FifoPage), ["rates"] = typeof(ExchangeRatesPage), ["tax"] = typeof(TaxCalculationPage),
        ["review"] = typeof(ReconciliationPage), ["reports"] = typeof(ReportsPage), ["history"] = typeof(HistoryPage), ["performance"] = typeof(PerformancePage)
    };
    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent(); DataContext = shell; Navigation.SetPageProviderService(new PageProvider(shell));
        shell.NavigateRequested += page => Navigate(page);
        Loaded += (_, _) => Navigate("dashboard"); Closing += OnClosing;
        // 子菜单也要接回普通页面，避免插件遮罩挡住切换后的业务内容。
        foreach(var item in NavigationEntries().Where(x=>x.TargetPageType is not null)) item.Click+=(_,_)=>HidePlugin();
        foreach(var item in Navigation.FooterMenuItems.OfType<NavigationViewItem>().Where(x=>x.TargetPageType is not null)) item.Click+=(_,_)=>HidePlugin();
        // 独立服务入口不依赖桌面插件成功加载；服务缺失也不影响税务和绩效页面。
        var backgroundServiceItem=CreateMenuItem("后台服务状态",SymbolRegular.Server24);
        backgroundServiceItem.Click+=async (_,_)=>await NavigateWorkerHealthAsync();
        TasksGroup.MenuItems.Add(backgroundServiceItem);
        var taskCenter=CreateMenuItem("任务中心",SymbolRegular.CalendarClock24);
        taskCenter.Click+=async (_,_)=>await NavigateScheduledTasksAsync();TasksGroup.MenuItems.Insert(0,taskCenter);
        Loaded+=(_,_)=>{shell.StatusBar.Start(taskPageLifetime.Token);pluginStartup=StartPluginsAsync();};
        Closed+=async (_,_)=> {forceClose=true;shell.StatusBar.Stop();taskPageLifetime.Cancel();PluginContent.Content=null;ExtensionsGroup.MenuItems.Clear();if(plugins is not null)await plugins.DisposeAsync();};
    }
    /// <summary>扩展页与内置页互斥显示；隐藏原内容但保留布局，避免收窄侧栏露出旧页面。</summary>
    private void ShowHostedPage(System.Windows.UIElement page)
    {
        Navigation.ApplyTemplate();
        builtInContent??=FindNavigationContent(Navigation)??throw new InvalidOperationException("导航内容容器未初始化。");
        builtInContent.Visibility=System.Windows.Visibility.Hidden;
        PluginContent.Content=page;
        PluginSurface.Visibility=System.Windows.Visibility.Visible;
    }
    private void HidePlugin()
    {
        PluginContent.Content=null;PluginSurface.Visibility=System.Windows.Visibility.Collapsed;
        if(builtInContent is not null)builtInContent.Visibility=System.Windows.Visibility.Visible;
    }
    /// <summary>按公开控件类型定位内容区，不依赖主题模板的私有字段和元素名称。</summary>
    private static System.Windows.UIElement? FindNavigationContent(System.Windows.DependencyObject parent)
    {
        if(parent is NavigationViewContentPresenter content)return content;
        for(var index=0;index<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);index++)
            if(FindNavigationContent(System.Windows.Media.VisualTreeHelper.GetChild(parent,index)) is { } child)return child;
        return null;
    }
    private async Task StartPluginsAsync()
    {
        // 登录后才建立插件会话；启停快照冻结到进程启动，重新解锁不提前应用禁用设置。
        var root=System.IO.Path.Combine(AppContext.BaseDirectory,"Plugins");
        var storage=Environment.GetCommandLineArgs().Contains("--smoke-test")
            ?System.IO.Path.Combine(System.IO.Path.GetTempPath(),"CRS.Plugin-smoke",Guid.NewGuid().ToString("N"))
            :System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CRS","plugins");
        try
        {
            // 插件启动前验证视觉资源契约，阻止缺失资源的皮肤进入工作区。
            PluginUiResources.Validate(System.Windows.Application.Current.Resources);
            if(!startupEnablement.TryGetValue(storage,out var enablement)) startupEnablement[storage]=enablement=CRS.PluginHost.PluginRuntime.ReadStartupEnablement(storage);
            plugins=new(root,storage,enablement,ValidatePluginPageAsync);await plugins.StartAsync();
            if(forceClose) {await plugins.DisposeAsync();return;}
            foreach(var page in plugins.Pages)
            {
                // 插件契约未声明图标时使用统一扩展图标，不按具体业务插件写分支。
                var item=CreateMenuItem(page.Contribution.Title,SymbolRegular.Apps24);item.Tag=page.Route;
                item.Click+=(_,_)=>
                {
                    try {Dispatcher.VerifyAccess();PluginUiResources.Validate(System.Windows.Application.Current.Resources);OpenNavigationGroup(ExtensionsGroup);ShowHostedPage(page.Contribution.CreateView());}
                    catch(Exception error) {HidePlugin();System.Windows.MessageBox.Show(this,"插件页面无法打开；类别："+error.GetType().Name,"插件状态");}
                };
                pluginItems.Add(item);ExtensionsGroup.MenuItems.Add(item);
            }
            var management=CreateMenuItem("插件管理",SymbolRegular.Settings24);management.ToolTip="插件管理：启停在重新启动后生效";
            management.Click+=(_,_)=>{OpenNavigationGroup(ExtensionsGroup);ShowPluginManagement();};pluginItems.Add(management);ExtensionsGroup.MenuItems.Add(management);
        }
        catch(Exception error) {System.Windows.MessageBox.Show(this,"插件宿主不可用，现有工作区可继续使用；类别："+error.GetType().Name,"插件状态");}
    }
    private void ShowPluginManagement()
    {
        // 展示与操作统一使用宿主状态，不按具体插件编写分支。
        ShowHostedPage(new PluginManagementPage(plugins!.Statuses,plugins.SetEnabledForNextStart,plugins.IsEnabledForNextStart));
    }
    /// <summary>底部状态入口与左侧菜单共用同一任务中心及健康页，不在桌面执行后台管理命令。</summary>
    private async void OnFooterTaskCenterClick(object sender,System.Windows.RoutedEventArgs e)=>await NavigateScheduledTasksAsync();
    private async void OnFooterRecentSyncClick(object sender,System.Windows.RoutedEventArgs e)=>await NavigateExecutionHistoryAsync(false);
    private async void OnFooterIssuesClick(object sender,System.Windows.RoutedEventArgs e)=>await NavigateExecutionHistoryAsync(true);
    private async void OnFooterServiceStatusClick(object sender,System.Windows.RoutedEventArgs e)=>await NavigateWorkerHealthAsync();

    /// <summary>右下角两个入口直达执行记录；异常模式的服务端筛选与分页由页面模型负责。</summary>
    public async Task NavigateExecutionHistoryAsync(bool issues)
    {
        OpenNavigationGroup(TasksGroup);
        if(forceClose)return;
        try
        {
            backgroundServiceModel??=new(taskPageLifetime.Token);
            var page=new BackgroundServicePage(backgroundServiceModel);
            page.ShowRecentExecutions();
            ShowHostedPage(page);
            await backgroundServiceModel.OpenExecutionHistoryAsync(issues);
        }
        catch(OperationCanceledException) { /* 关闭或锁定时不继续查询。 */ }
        catch(Exception error) {if(!forceClose)System.Windows.MessageBox.Show(this,"执行记录暂不可用；类别："+error.GetType().Name,"任务中心");}
    }

    public async Task NavigateWorkerHealthAsync()
    {
        OpenNavigationGroup(TasksGroup);
        if(forceClose)return;
        try
        {
            backgroundServiceModel??=new(taskPageLifetime.Token);
            ShowHostedPage(new WorkerStatusPage(backgroundServiceModel));
            await backgroundServiceModel.RefreshCommand.ExecuteAsync(null);
        }
        catch(OperationCanceledException) { /* 锁定/退出时不回写。 */ }
        catch(Exception error) {if(!forceClose)System.Windows.MessageBox.Show(this,"服务状态暂不可用；类别："+error.GetType().Name,"后台服务");}
    }

    public async Task NavigateScheduledTasksAsync()
    {
        OpenNavigationGroup(TasksGroup);
        if(forceClose)return;
        try
        {
            backgroundServiceModel??=new(taskPageLifetime.Token);
            var page=new BackgroundServicePage(backgroundServiceModel);page.ShowCurrentTasks();ShowHostedPage(page);
            await backgroundServiceModel.RefreshCommand.ExecuteAsync(null);
        }
        catch(OperationCanceledException){ /* 锁定或关闭后不回写页面。 */ }
        catch(Exception error){if(!forceClose)System.Windows.MessageBox.Show(this,"计划任务暂不可用；类别："+error.GetType().Name,"计划任务");}
    }
    /// <summary>候选升级实际载入页面和资源后才确认，不展示探测窗口或税务内容。</summary>
    private async Task ValidatePluginPageAsync(CRS.PluginHost.HostedPage page,CancellationToken token)
    {
        Dispatcher.VerifyAccess();token.ThrowIfCancellationRequested();
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);
        var view=page.Contribution.CreateView();
        var probe=new System.Windows.Window {Content=view,Width=1200,Height=800,ShowActivated=false,ShowInTaskbar=false,Opacity=0,Owner=this};
        try
        {
            probe.Show();await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            token.ThrowIfCancellationRequested();probe.UpdateLayout();
            if(view.ActualWidth<=0 || view.ActualHeight<=0)throw new InvalidOperationException("候选插件页面未完成布局。");
        }
        finally {probe.Content=null;probe.Close();}
    }
    public bool Navigate(string key)
    {
        HidePlugin();
        if (!PageTypes.TryGetValue(key, out var type)) return false;
        foreach(var group in Navigation.MenuItems.OfType<NavigationViewItem>().Where(x=>x.MenuItems.OfType<NavigationViewItem>().Any(child=>child.TargetPageType==type)))
            OpenNavigationGroup(group);
        if (Navigation.SelectedItem is NavigationViewItem item && item.TargetPageType == type) return true;
        return Navigation.Navigate(type);
    }
    public bool IsNavigationExpanded { get => Navigation.IsPaneOpen; set => Navigation.IsPaneOpen = value; }
    /// <summary>动态入口与静态菜单使用相同图标和悬停标题。</summary>
    private static NavigationViewItem CreateMenuItem(string title,SymbolRegular symbol)=>new()
    {Content=title,Icon=new SymbolIcon {Symbol=symbol},ToolTip=title};
    /// <summary>遍历两级菜单，让页面切换同时覆盖分类中的原有入口。</summary>
    private IEnumerable<NavigationViewItem> NavigationEntries()=>Navigation.MenuItems.OfType<NavigationViewItem>()
        .SelectMany(item=>new[]{item}.Concat(item.MenuItems.OfType<NavigationViewItem>()));
    /// <summary>点击分类只改变展开状态；子项的冒泡点击不再次折叠分类。</summary>
    private void OnNavigationGroupClick(object sender,System.Windows.RoutedEventArgs e)
    {
        if(!ReferenceEquals(e.Source,sender) || sender is not NavigationViewItem group || !group.IsExpanded)return;
        OpenNavigationGroup(group);
    }
    /// <summary>展开当前分类并收起其余分类，侧栏收窄时由控件显示子项弹出菜单。</summary>
    private void OpenNavigationGroup(NavigationViewItem selected)
    {
        foreach(var group in Navigation.MenuItems.OfType<NavigationViewItem>().Where(x=>x.MenuItems.Count>0))
            group.IsExpanded=Navigation.IsPaneOpen && ReferenceEquals(group,selected);
    }
    /// <summary>统一标题栏折叠入口。继续由NavigationView管理侧栏状态，插件偏移及品牌可见性复用同一IsPaneOpen绑定。</summary>
    private void OnNavigationToggleClick(object sender,System.Windows.RoutedEventArgs e)
    {
        Navigation.IsPaneOpen=!Navigation.IsPaneOpen;
    }
    private void OnSettings(object sender, System.Windows.RoutedEventArgs e)
    {
        var shell = (ShellViewModel)DataContext;
        if (!shell.State.IsIdle || shell.Settings is null) return;
        try { shell.Settings.Reload(); var dialog=new SettingsWindow(shell.Settings) { Owner = this };dialog.ShowDialog();if(dialog.RequiresPhoneEnrollment) LockRequested?.Invoke();else if(dialog.OpenDatabaseManagement) SecurityRequested?.Invoke(); }
        catch (Exception error) { shell.State.UseCases.LogFailure(error.GetType().Name); shell.State.Interaction.ShowError("无法读取设置，请检查本机设置文件和目录权限。"); }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if(forceClose) return;
        var shell = (ShellViewModel)DataContext;
        if (Environment.GetCommandLineArgs().Contains("--smoke-test")) { shell.Import.Token = ""; return; }
        if (shell.State.IsBusy) { e.Cancel = true; System.Windows.MessageBox.Show(this, "请等待当前前台操作结束，或返回执行该操作的页面进行取消。", "操作正在进行"); }
        else shell.Import.Token = "";
    }
}
