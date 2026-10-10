using System.Windows;
using CRS.DesktopClient.Composition;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;

namespace CRS.DesktopClient;

public partial class App : System.Windows.Application
{
    private CRS.Application.Abstractions.IVaultService? vault;
    private CRS.Application.Abstractions.IDesktopSettingsService? vaultSettings;
    private MainWindow? workspace;
    private bool locking;
    private System.Windows.Threading.DispatcherTimer? idle;
    private long lastInput=System.Diagnostics.Stopwatch.GetTimestamp();
    private VaultWindow? securityDialog;
    /// <summary>组合根连接后台和前台，界面不直接构造解析器或数据库。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 启动向导和锁定切换不应被最后一个旧窗口关闭触发自动退出。
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        Microsoft.Win32.SystemEvents.SessionSwitch+=SessionChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged+=PowerChanged;
        if (e.Args.Contains("--smoke-test"))
        {
            DispatcherUnhandledException += (_, failure) =>
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CRS-UI-smoke-error.txt"), failure.Exception.ToString());
                failure.Handled = true; Shutdown(1);
            };
        }
        try
        {
            var smoke = e.Args.Contains("--smoke-test");
            if (smoke) Console.Error.WriteLine("smoke: isolated store");
            if(!smoke)
            {
                vault=DesktopComposition.CreateVault(); vaultSettings=DesktopComposition.CreateSettings();
                OpenWorkspace(); return;
            }
            var runtime = DesktopComposition.Create(true);
            vault=runtime.Vault;
            var viewModel = new ShellViewModel(runtime.UseCases, smoke ? new SmokeInteraction() : new UserInteraction(), runtime.Settings);
            if (smoke) Console.Error.WriteLine("smoke: create navigation shell");
            var window = new MainWindow(viewModel); MainWindow = window;
            if (smoke)
            {
                window.Loaded += async (_, _) =>
                {
                    try { await SmokeCheck.RunAsync(window, viewModel, runtime.Operations, e.Args,runtime.Vault,runtime.Settings); Shutdown(0); }
                    catch (Exception error) { System.IO.File.WriteAllText(System.IO.Path.Combine(runtime.DirectoryPath, "smoke-error.txt"), error.ToString()); Shutdown(1); }
                };
            }
            window.Show();
            if (smoke) Console.Error.WriteLine("smoke: window shown");
            if (!smoke) _ = viewModel.History.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception error)
        {
            if (e.Args.Contains("--smoke-test")) System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CRS-UI-smoke-error.txt"), error.ToString());
            DesktopComposition.LogStartupFailure(error);
            if (!e.Args.Contains("--smoke-test")) MessageBox.Show("程序无法启动，请检查本地数据目录及配置文件。", ProductIdentity.Name);
            Shutdown(1);
        }
    }
    /// <summary>每次解锁都建立全新的工作区和仓储会话，旧后台任务无法借新会话继续提交。</summary>
    private void OpenWorkspace()
    {
        var security=new VaultViewModel(vault!,vaultSettings!,new UserInteraction());
        var gate=new VaultWindow(security); MainWindow=gate;
        if(gate.ShowDialog()!=true || !vault!.IsUnlocked) {vault!.Lock(); Shutdown(); return;}
        OpenAuthenticatedWorkspace();
    }
    /// <summary>手机旋转会销毁原业务会话，重新认证后必须重建整个工作区。</summary>
    private void OpenAuthenticatedWorkspace()
    {
        var runtime=DesktopComposition.Create(false,vault,vaultSettings);
        var shell=new ShellViewModel(runtime.UseCases,new UserInteraction(),runtime.Settings);
        workspace=new MainWindow(shell); MainWindow=workspace;
        workspace.SecurityRequested+=()=>
        {
            var model=new VaultViewModel(vault!,vaultSettings!,new UserInteraction());
            var dialog=new VaultWindow(model) {Owner=workspace}; securityDialog=dialog;
            var restricted=false;
            model.SessionRestricted+=()=> {restricted=true; workspace.PrepareLock();};
            model.LockRequested+=()=> {dialog.Close(); Dispatcher.BeginInvoke(LockWorkspace);};
            var accepted=dialog.ShowDialog(); securityDialog=null;
            if(restricted && workspace is not null)
            {
                locking=true; idle?.Stop(); System.Windows.Input.InputManager.Current.PreProcessInput-=InputActivity;
                workspace.Close(); workspace=null; locking=false;
                if(accepted==true && vault!.IsUnlocked) OpenAuthenticatedWorkspace();
                else {vault!.Lock(); OpenWorkspace();}
            }
        };
        workspace.LockRequested+=LockWorkspace;
        workspace.Closed+=(_,_)=> {if(!locking) Shutdown();};
        System.Windows.Input.InputManager.Current.PreProcessInput+=InputActivity;
        lastInput=System.Diagnostics.Stopwatch.GetTimestamp(); workspace.Show(); _=shell.History.RefreshCommand.ExecuteAsync(null);
        idle=new(TimeSpan.FromSeconds(10),System.Windows.Threading.DispatcherPriority.Background,(_,_)=> {if(System.Diagnostics.Stopwatch.GetElapsedTime(lastInput)>=TimeSpan.FromMinutes(10)) LockWorkspace();},Dispatcher);
    }
    private void InputActivity(object sender,System.Windows.Input.PreProcessInputEventArgs e)
    {
        // 排除布局触发的 QueryCursor 和焦点事件；使用单调时钟，不让系统校时延长解锁时间。
        var input=e.StagingItem.Input;
        if(input is System.Windows.Input.KeyEventArgs or System.Windows.Input.MouseButtonEventArgs or System.Windows.Input.MouseWheelEventArgs or System.Windows.Input.TouchEventArgs
            || input.RoutedEvent==System.Windows.Input.Mouse.MouseMoveEvent) lastInput=System.Diagnostics.Stopwatch.GetTimestamp();
    }
    private void SessionChanged(object sender,Microsoft.Win32.SessionSwitchEventArgs e) {if(e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.SessionLogoff) Dispatcher.BeginInvoke(LockWorkspace);}
    private void PowerChanged(object sender,Microsoft.Win32.PowerModeChangedEventArgs e) {if(e.Mode is Microsoft.Win32.PowerModes.Suspend or Microsoft.Win32.PowerModes.Resume) Dispatcher.BeginInvoke(LockWorkspace);}
    /// <summary>立即隐藏敏感窗口，取消后台请求并销毁会话；不复用已锁定的模型或密钥。</summary>
    private void LockWorkspace()
    {
        if(locking) return;
        // 创建／恢复向导也可能显示恢复秘密；系统锁屏时关闭启动层，重开应用后必须重新验证。
        if(workspace is null) {vault?.Lock(); foreach(Window window in Windows) window.Hide(); Shutdown(); return;}
        locking=true; idle?.Stop();
        System.Windows.Input.InputManager.Current.PreProcessInput-=InputActivity;
        workspace.Hide(); securityDialog?.AbortForLock(); securityDialog=null;
        workspace.PrepareLock(); vault?.Lock(); workspace.Close(); workspace=null;
        locking=false; OpenWorkspace();
    }
    /// <summary>退出时清空日志资源，令牌随窗口会话释放。</summary>
    protected override void OnExit(ExitEventArgs e) {idle?.Stop(); Microsoft.Win32.SystemEvents.SessionSwitch-=SessionChanged; Microsoft.Win32.SystemEvents.PowerModeChanged-=PowerChanged; System.Windows.Input.InputManager.Current.PreProcessInput-=InputActivity; vault?.Dispose(); DesktopComposition.Shutdown(); base.OnExit(e); }
}
