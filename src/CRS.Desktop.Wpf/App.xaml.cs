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
    private readonly AutomaticLockPolicy automaticLock=new();
    private bool awaitingUnlock;
    private VaultWindow? securityDialog;
    private DatabaseBackupWindow? databaseDialog;
    /// <summary>组合根连接后台和前台，界面不直接构造解析器或数据库。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        try {TrustedDocumentAssembly.Load();}
        catch(Exception)
        {
            MessageBox.Show("文档组件缺失、被替换或未经验证。为保护报税资料，程序已停止启动，请重新安装完整程序包。",ProductIdentity.Name);
            Shutdown(1);return;
        }
        StartVerified(e);
    }
    // 与校验入口隔离 JIT，组件通过验证前不进入业务组合和文档调用。
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void StartVerified(StartupEventArgs e)
    {
        base.OnStartup(e);
        // WPF-UI 的主按钮、导航选中与焦点色均从品牌调色板取得，禁止跟随操作系统蓝色。
        if (Resources["BrandAccentBrush"] is System.Windows.Media.SolidColorBrush brandAccent)
            Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(brandAccent.Color, Wpf.Ui.Appearance.ApplicationTheme.Light);
        // 启动向导和锁定切换不应被最后一个旧窗口关闭触发自动退出。
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        Microsoft.Win32.SystemEvents.SessionSwitch+=SessionChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged+=PowerChanged;
        if (e.Args.Contains("--smoke-test"))
        {
            DispatcherUnhandledException += (_, failure) =>
            {
                // 即使是窗口验收诊断也不落盘完整异常，避免原件路径和业务内容泄露。
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CRS-UI-smoke-error.txt"), CRS.Application.SensitiveDiagnostics.FormatError(failure.Exception));
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
            var viewModel = new ShellViewModel(runtime.UseCases, smoke ? new SmokeInteraction() : new UserInteraction(), runtime.Settings,runtime.Vault,runtime.Performance,cleanup:smoke?null:new WorkerDataCleanupGateway(),aiModels:runtime.AiModels);
            if (smoke) Console.Error.WriteLine("smoke: create navigation shell");
            var window = new MainWindow(viewModel); MainWindow = window;
            if (smoke)
            {
                window.Loaded += async (_, _) =>
                {
                    try { await SmokeCheck.RunAsync(window, viewModel, runtime.Operations, e.Args,runtime.Vault,runtime.Settings); Shutdown(0); }
                    catch (Exception error) { System.IO.File.WriteAllText(System.IO.Path.Combine(runtime.DirectoryPath, "smoke-error.txt"), CRS.Application.SensitiveDiagnostics.FormatError(error)); Shutdown(1); }
                };
            }
            window.Show();
            if (smoke) Console.Error.WriteLine("smoke: window shown");
            if (!smoke) _ = viewModel.History.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception error)
        {
            if (e.Args.Contains("--smoke-test")) System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CRS-UI-smoke-error.txt"), CRS.Application.SensitiveDiagnostics.FormatError(error));
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
        securityDialog=gate;
        var accepted=gate.ShowDialog();securityDialog=null;
        if(accepted!=true || !vault!.IsUnlocked) {vault!.Lock(); Shutdown(); return;}
        OpenAuthenticatedWorkspace();
    }
    /// <summary>完整身份验证后建立工作区；数据库管理使用独立窗口。</summary>
    private void OpenAuthenticatedWorkspace()
    {
        var runtime=DesktopComposition.Create(false,vault,vaultSettings);
        // 解锁后重新装配桌面会话，恢复该 Windows 用户已保存的模型选择。
        var shell=new ShellViewModel(runtime.UseCases,new UserInteraction(),runtime.Settings,runtime.Vault,runtime.Performance,cleanup:new WorkerDataCleanupGateway(),aiModels:runtime.AiModels);
        workspace=new MainWindow(shell); MainWindow=workspace;
        workspace.DatabaseManagementRequested+=()=>OpenDatabaseManagement(runtime.DirectoryPath);
        workspace.LockRequested+=LockWorkspace;
        workspace.Closed+=(_,_)=> {if(!locking) Shutdown();};
        System.Windows.Input.InputManager.Current.PreProcessInput+=InputActivity;
        automaticLock.RecordActivity(); workspace.Show(); _=shell.History.RefreshCommand.ExecuteAsync(null);
        idle=new(TimeSpan.FromSeconds(1),System.Windows.Threading.DispatcherPriority.Input,(_,_)=> {if(automaticLock.IsExpired) LockWorkspace();},Dispatcher);
    }
    private void OpenDatabaseManagement(string currentDirectory)
    {
        if(workspace is null||vault is not{IsUnlocked:true}||vaultSettings is null)return;
        var currentWorkspace=workspace;
        var model=new DatabaseBackupViewModel(vault,vaultSettings,currentDirectory);
        var dialog=new DatabaseBackupWindow(model){Owner=currentWorkspace};databaseDialog=dialog;
        model.RestoreStarting+=()=>
        {
            idle?.Stop();System.Windows.Input.InputManager.Current.PreProcessInput-=InputActivity;
            currentWorkspace.PrepareLock();vault.Lock();
        };
        try{dialog.ShowDialog();}finally{databaseDialog=null;}
        if(model.RestoreStarted&&ReferenceEquals(workspace,currentWorkspace))
        {
            locking=true;currentWorkspace.Close();workspace=null;locking=false;
            OpenWorkspace();
        }
    }
    private void InputActivity(object sender,System.Windows.Input.PreProcessInputEventArgs e)
    {
        // 排除布局触发的 QueryCursor 和焦点事件；使用单调时钟，不让系统校时延长解锁时间。
        var input=e.StagingItem.Input;
        if(input is System.Windows.Input.KeyEventArgs or System.Windows.Input.MouseButtonEventArgs or System.Windows.Input.MouseWheelEventArgs or System.Windows.Input.TouchEventArgs
            || input.RoutedEvent==System.Windows.Input.Mouse.MouseMoveEvent) automaticLock.RecordActivity();
    }
    private void SessionChanged(object sender,Microsoft.Win32.SessionSwitchEventArgs e)
    {if(AutomaticLockPolicy.RequiresLock(e.Reason)) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,LockWorkspace);}
    private void PowerChanged(object sender,Microsoft.Win32.PowerModeChangedEventArgs e)
    {if(AutomaticLockPolicy.RequiresLock(e.Mode)) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,LockWorkspace);}
    /// <summary>立即隐藏敏感窗口，取消后台请求并销毁会话；不复用已锁定的模型或密钥。</summary>
    private void LockWorkspace()
    {
        if(locking) return;
        // 休眠与恢复等事件可连续到达，已经锁定时保持遮挡，不重复退出或自动进入。
        if(awaitingUnlock) {vault?.Lock();securityDialog?.AbortForLock();databaseDialog?.AbortForLock();return;}
        // 创建／恢复向导也可能显示恢复秘密；系统锁屏时关闭启动层，重开应用后必须重新验证。
        if(workspace is null) {foreach(Window window in Windows) window.Hide(); securityDialog?.AbortForLock();databaseDialog?.AbortForLock(); vault?.Lock(); Shutdown(); return;}
        locking=true; idle?.Stop();
        System.Windows.Input.InputManager.Current.PreProcessInput-=InputActivity;
        workspace.Hide(); securityDialog?.AbortForLock(); securityDialog=null;
        databaseDialog?.AbortForLock();databaseDialog=null;
        workspace.PrepareLock(); vault?.Lock(); workspace.Close(); workspace=null;
        locking=false; awaitingUnlock=true; try {OpenWorkspace();} finally {awaitingUnlock=false;}
    }
    /// <summary>退出时清空日志资源，令牌随窗口会话释放。</summary>
    protected override void OnExit(ExitEventArgs e) {idle?.Stop(); Microsoft.Win32.SystemEvents.SessionSwitch-=SessionChanged; Microsoft.Win32.SystemEvents.PowerModeChanged-=PowerChanged; System.Windows.Input.InputManager.Current.PreProcessInput-=InputActivity; vault?.Dispose(); DesktopComposition.Shutdown(); base.OnExit(e); }
}
