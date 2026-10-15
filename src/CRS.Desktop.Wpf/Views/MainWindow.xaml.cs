using System.ComponentModel;
using CRS.DesktopClient.Services;
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
    private ScheduledTasksViewModel? taskPageModel;
    private Task? pluginStartup;
    private readonly List<NavigationViewItem> pluginItems = [];
    private static readonly Dictionary<string,IReadOnlySet<string>> startupEnablement=[];
    public event Action? SecurityRequested;
    public event Action? LockRequested;
    private void OnLock(object sender,System.Windows.RoutedEventArgs e)=>LockRequested?.Invoke();
    /// <summary>安全锁定独立于普通关闭，取消请求、清空令牌并解除敏感页面引用。</summary>
    public void PrepareLock()
    {
        forceClose=true;
        taskPageLifetime.Cancel();
        plugins?.Cancel(); PluginContent.Content=null; PluginSurface.Visibility=System.Windows.Visibility.Collapsed;
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
        foreach(var item in Navigation.MenuItems.OfType<NavigationViewItem>()) item.Click+=(_,_)=>HidePlugin();
        foreach(var item in Navigation.FooterMenuItems.OfType<NavigationViewItem>().Where(x=>x.TargetPageType is not null)) item.Click+=(_,_)=>HidePlugin();
        Loaded+=(_,_)=>{pluginStartup=StartPluginsAsync();};
        Closed+=async (_,_)=> {forceClose=true;taskPageLifetime.Cancel();PluginContent.Content=null;foreach(var item in pluginItems) Navigation.MenuItems.Remove(item);if(plugins is not null)await plugins.DisposeAsync();};
    }
    private void HidePlugin() {PluginContent.Content=null;PluginSurface.Visibility=System.Windows.Visibility.Collapsed;}
    private async Task StartPluginsAsync()
    {
        // 登录后才建立插件会话；启停快照冻结到进程启动，重新解锁不提前应用禁用设置。
        var root=System.IO.Path.Combine(AppContext.BaseDirectory,"Plugins");
        var storage=Environment.GetCommandLineArgs().Contains("--smoke-test")
            ?System.IO.Path.Combine(System.IO.Path.GetTempPath(),"CRS.Plugin-smoke",Guid.NewGuid().ToString("N"))
            :System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CRS","plugins");
        try
        {
            if(!startupEnablement.TryGetValue(storage,out var enablement)) startupEnablement[storage]=enablement=CRS.PluginHost.PluginRuntime.ReadStartupEnablement(storage);
            plugins=new(root,storage,enablement,ValidatePluginPageAsync);await plugins.StartAsync();
            if(forceClose) {await plugins.DisposeAsync();return;}
            foreach(var page in plugins.Pages)
            {
                var item=new NavigationViewItem {Content=page.Contribution.Title,Tag=page.Route};
                item.Click+=(_,_)=>
                {
                    try {Dispatcher.VerifyAccess();PluginContent.Content=page.Contribution.CreateView();PluginSurface.Margin=new(Navigation.IsPaneOpen?206:56,0,0,0);PluginSurface.Visibility=System.Windows.Visibility.Visible;}
                    catch(Exception error) {HidePlugin();System.Windows.MessageBox.Show(this,"插件页面无法打开；类别："+error.GetType().Name,"插件状态");}
                };
                pluginItems.Add(item);Navigation.MenuItems.Add(item);
            }
            var management=new NavigationViewItem {Content="插件管理（重启生效）"};
            management.Click+=(_,_)=>ShowPluginManagement();pluginItems.Add(management);Navigation.MenuItems.Add(management);
            var tasks=new NavigationViewItem {Content="计划任务"};
            tasks.Click+=async (_,_)=>await NavigateScheduledTasksAsync();pluginItems.Add(tasks);Navigation.MenuItems.Add(tasks);
        }
        catch(Exception error) {System.Windows.MessageBox.Show(this,"插件宿主不可用，现有工作区可继续使用；类别："+error.GetType().Name,"插件状态");}
    }
    private void ShowPluginManagement()
    {
        // 只根据通用状态构造管理入口，不按业务插件ID编写分支。
        var panel=new System.Windows.Controls.StackPanel {Margin=new(24)};
        panel.Children.Add(new System.Windows.Controls.TextBlock {Text="官方预装插件 · 启用和禁用在重新启动程序后生效",FontSize=20,TextWrapping=System.Windows.TextWrapping.Wrap});
        foreach(var status in plugins!.Statuses)
        {
            var row=new System.Windows.Controls.StackPanel {Margin=new(0,12,0,0)};
            row.Children.Add(new System.Windows.Controls.TextBlock {Text=$"{status.Name} · {status.State} · {status.Error}\n{status.RecoveryMessage}",TextWrapping=System.Windows.TextWrapping.Wrap});
            var button=new System.Windows.Controls.Button {Content=status.State==CRS.PluginHost.PluginState.Disabled?"下次启动启用":"下次启动禁用"};
            var enable=status.State==CRS.PluginHost.PluginState.Disabled;
            button.Click+=(_,_)=> {try {plugins.SetEnabledForNextStart(status.Id,enable);button.Content="已保存，重新启动后生效";button.IsEnabled=false;}catch(Exception error){button.Content="保存失败："+error.GetType().Name;}};
            row.Children.Add(button);panel.Children.Add(row);
        }
        PluginContent.Content=panel;PluginSurface.Margin=new(Navigation.IsPaneOpen?206:56,0,0,0);PluginSurface.Visibility=System.Windows.Visibility.Visible;
    }
    public async Task NavigateScheduledTasksAsync()
    {
        if(pluginStartup is not null)await pluginStartup;
        if(plugins is null || forceClose)return;
        try
        {
            taskPageModel??=new(plugins.Schedules,taskPageLifetime.Token);
            await taskPageModel.LoadAsync(taskPageLifetime.Token);
            if(forceClose)return;
            PluginContent.Content=new ScheduledTasksPage(taskPageModel);PluginSurface.Margin=new(Navigation.IsPaneOpen?206:56,0,0,0);PluginSurface.Visibility=System.Windows.Visibility.Visible;
        }
        catch(OperationCanceledException){ /* 锁定或关闭后不回写页面。 */ }
        catch(Exception error){if(!forceClose)System.Windows.MessageBox.Show(this,"计划任务暂不可用；类别："+error.GetType().Name,"计划任务");}
    }
    /// <summary>候选升级实际载入页面和资源后才确认，不展示探测窗口或税务内容。</summary>
    private async Task ValidatePluginPageAsync(CRS.PluginHost.HostedPage page,CancellationToken token)
    {
        Dispatcher.VerifyAccess();token.ThrowIfCancellationRequested();
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
        if (Navigation.SelectedItem is NavigationViewItem item && item.TargetPageType == type) return true;
        return Navigation.Navigate(type);
    }
    public bool IsNavigationExpanded { get => Navigation.IsPaneOpen; set => Navigation.IsPaneOpen = value; }
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
        if (shell.State.IsBusy) { e.Cancel = true; System.Windows.MessageBox.Show(this, "请等待操作结束，或先取消当前操作。", "操作正在进行"); }
        else shell.Import.Token = "";
    }
}
