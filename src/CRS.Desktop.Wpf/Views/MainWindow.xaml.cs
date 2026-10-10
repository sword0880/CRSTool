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
    public event Action? SecurityRequested;
    public event Action? LockRequested;
    private void OnSecurity(object sender,System.Windows.RoutedEventArgs e) {if(((ShellViewModel)DataContext).State.IsIdle) SecurityRequested?.Invoke();}
    private void OnLock(object sender,System.Windows.RoutedEventArgs e)=>LockRequested?.Invoke();
    /// <summary>安全锁定独立于普通关闭，取消请求、清空令牌并解除敏感页面引用。</summary>
    public void PrepareLock()
    {
        forceClose=true;
        // 换手机限制会话与系统锁屏可能先后触发，已清理窗口应允许重复关闭。
        if(DataContext is not ShellViewModel shell) return;
        foreach(var settings in OwnedWindows.OfType<SettingsWindow>().ToArray()) settings.AbortForLock();
        shell.Settings?.ClearSecurityInputs();
        shell.State.UseCases.EndSession(); shell.Import.CancelCommand.Execute(null); shell.History.CancelOperationCommand.Execute(null);
        shell.Import.Token=""; shell.State.Clear(); DataContext=null;
    }
    public static readonly IReadOnlyDictionary<string, Type> PageTypes = new Dictionary<string, Type>
    {
        ["dashboard"] = typeof(DashboardPage), ["import"] = typeof(ImportPage), ["trades"] = typeof(TradesPage),
        ["fifo"] = typeof(FifoPage), ["rates"] = typeof(ExchangeRatesPage), ["tax"] = typeof(TaxCalculationPage),
        ["review"] = typeof(ReconciliationPage), ["reports"] = typeof(ReportsPage), ["history"] = typeof(HistoryPage)
    };
    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent(); DataContext = shell; Navigation.SetPageProviderService(new PageProvider(shell));
        shell.NavigateRequested += page => Navigate(page);
        Loaded += (_, _) => Navigate("dashboard"); Closing += OnClosing;
    }
    public bool Navigate(string key)
    {
        if (!PageTypes.TryGetValue(key, out var type)) return false;
        if (Navigation.SelectedItem is NavigationViewItem item && item.TargetPageType == type) return true;
        return Navigation.Navigate(type);
    }
    public bool IsNavigationExpanded { get => Navigation.IsPaneOpen; set => Navigation.IsPaneOpen = value; }
    private void OnSettings(object sender, System.Windows.RoutedEventArgs e)
    {
        var shell = (ShellViewModel)DataContext;
        if (!shell.State.IsIdle || shell.Settings is null) return;
        try { shell.Settings.Reload(); new SettingsWindow(shell.Settings) { Owner = this }.ShowDialog(); }
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
