using CRS.Application.Abstractions;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class VaultInteractionChecks
{
    /// <summary>离线恢复确认不能绕过，切换目录不能沿用解锁，密码操作完成后不保留输入。</summary>
    public static async Task RunAsync()
    {
        var vault=new FakeVault(); var settings=new Settings(); var model=new VaultViewModel(vault,settings,new Interaction()); var ready=0;
        // 尚未创建库时必须显示首次设置，启动预检不能获得业务访问权限。
        if(await VaultStartup.TryEnterAsync(vault,model.Directory) || vault.LocalUnlocks!=0) throw new Exception("首次设置被启动预检跳过。");
        model.Ready+=()=>ready++; model.Password="Synthetic-New-Password";
        if(!model.ShowDirectoryEntry) throw new Exception("首次创建没有提供数据库位置选择。");
        await model.CreateCommand.ExecuteAsync(null);
        if(model.ContinueCommand.CanExecute(null) || model.Password!="" || model.RecoveryKey=="") throw new Exception("创建后未确认恢复秘密就可进入，或仍保留主密码。");
        model.RecoverySaved=true; if(model.ContinueCommand.CanExecute(null)) throw new Exception("扫码验证前可进入。");
        model.Code="123456"; await model.VerifyCodeCommand.ExecuteAsync(null);
        if(model.ContinueCommand.CanExecute(null) || model.EnrollmentQr is not null || model.Code!="") throw new Exception("手机恢复码尚未确认就放行或验证秘密未清理。");
        model.MfaRecoverySaved=true; if(!model.ContinueCommand.CanExecute(null)) throw new Exception("保存两种恢复秘密后无法进入。");
        var initial=model.Directory; model.Directory="D:/other-vault"; if(model.ContinueCommand.CanExecute(null)) throw new Exception("改目录仍沿用旧解锁。");
        model.Directory=initial; await model.ContinueCommand.ExecuteAsync(null);
        if(ready!=1 || model.RecoveryKey!="" || settings.Current.DatabaseDirectory!=initial) throw new Exception("进入工作区未清理恢复显示或保存目录。");
        model.LockCommand.Execute(null); if(vault.IsUnlocked || model.ContinueCommand.CanExecute(null)) throw new Exception("锁定后可继续进入。");
        var daily=new VaultViewModel(vault,settings,new Interaction()); await daily.InitializeAsync();
        if(daily.IsNewVault || daily.ShowPasswordEntry || daily.ShowDirectoryEntry || daily.CreateCommand.CanExecute(null) || !daily.VerifyCodeCommand.CanExecute(null)
            || daily.ShowMaintenance || daily.IsUnlocked || vault.LocalUnlocks!=1 || vault.PasswordUnlocks!=0) throw new Exception("已有本机凭证仍要求创建／输入主密码，或未经手机验证已经进入。");
        var dailyEntered=0; daily.Ready+=()=>dailyEntered++;
        daily.ShowDirectoryOptions=true; if(!daily.ShowDirectoryEntry) throw new Exception("主动恢复／切换位置时无法查看目录。");
        daily.ShowDirectoryOptions=false;
        daily.Code="123456"; await daily.VerifyCodeCommand.ExecuteAsync(null); if(!vault.IsUnlocked || dailyEntered!=1 || daily.Code!="") throw new Exception("仅手机验证码日常登录未直接进入或未清理验证码。");
        // 容量提示在达到 75% 时预警，超限明确拒绝，不暗示原库会自动清理。
        vault.BackupCapacity=new(1536L*1024*1024,2L*1024*1024*1024);
        if(!new VaultViewModel(vault,settings,new Interaction()).BackupCapacityHint.Contains("接近上限")) throw new Exception("接近备份上限没有预警。");
        vault.BackupCapacity=new(2L*1024*1024*1024+4096,2L*1024*1024*1024);
        if(!new VaultViewModel(vault,settings,new Interaction()).BackupCapacityHint.Contains("超出备份上限")) throw new Exception("备份超限没有明确提示。");
        vault.BackupCapacity=null;
        vault.Lock(); vault.LocalAvailable=false;
        var recovery=new VaultViewModel(vault,settings,new Interaction()); await recovery.InitializeAsync();
        if(!recovery.ShowPasswordEntry || recovery.IsNewVault || vault.LocalUnlocks!=1) throw new Exception("本机凭证丢失时未提供原主密码恢复或重新要求创建库。");
        vault.LocalAvailable=true;
        await vault.VerifyTotpAsync("123456");
        var state=new WorkspaceState(new FakeUseCases(),new Interaction()); var securitySettings=new SettingsViewModel(state,settings,vault);
        await securitySettings.CheckHelloAvailabilityAsync();
        if(!securitySettings.SecuritySettingsAvailable || !securitySettings.PhoneVerificationEnabled || securitySettings.WindowsHelloEnabled || securitySettings.WindowsHelloAvailable) throw new Exception("未配置 Hello 时没有限定手机验证。");
        state.IsBusy=true; if(securitySettings.SavePhoneVerificationCommand.CanExecute(null)) throw new Exception("后台忙碌仍能改变验证方式。"); state.IsBusy=false;
        securitySettings.SecurityPassword="synthetic-password"; securitySettings.SecurityCode="123456";
        await securitySettings.SavePhoneVerificationCommand.ExecuteAsync(null);
        if(!vault.PhoneVerificationEnabled || securitySettings.SecurityPassword!="" || securitySettings.SecurityCode!="") throw new Exception("设置未保存或未清理凭证。");
        vault.HelloSupported=true;await securitySettings.CheckHelloAvailabilityAsync();
        securitySettings.WindowsHelloEnabled=true;
        if(!securitySettings.HasLoginMethodChanges || !securitySettings.SecurityMessage.Contains("尚未保存") || vault.UsesWindowsHello) throw new Exception("尚未验证保存的选择被误报为已启用。");
        var savedLogRetention=settings.Current.LogRetentionFiles;securitySettings.LogRetentionFiles=savedLogRetention+1;
        await securitySettings.SaveCommand.ExecuteAsync(null);
        if(!securitySettings.SecurityMessage.Contains("请输入") || !securitySettings.WindowsHelloEnabled || vault.UsesWindowsHello || settings.Current.LogRetentionFiles!=savedLogRetention) throw new Exception("缺主密码时未提示，或误保存了设置。");
        securitySettings.SecurityPassword="synthetic-password";await securitySettings.SavePhoneVerificationCommand.ExecuteAsync(null);
        if(!securitySettings.SecurityMessage.Contains("六位") || vault.UsesWindowsHello || !securitySettings.HasLoginMethodChanges) throw new Exception("缺手机代码时没有提示并保留待保存选项。");
        securitySettings.SecurityCode="abcdef";await securitySettings.SavePhoneVerificationCommand.ExecuteAsync(null);
        if(!securitySettings.SecurityMessage.Contains("六位") || vault.UsesWindowsHello) throw new Exception("非数字手机代码进入了系统验证。");
        securitySettings.SecurityPassword="synthetic-password";securitySettings.SecurityCode="123456";
        vault.CancelLoginChange=true;await securitySettings.SaveCommand.ExecuteAsync(null);
        if(vault.UsesWindowsHello || securitySettings.WindowsHelloEnabled || !securitySettings.SecurityMessage.Contains("未修改")) throw new Exception("取消验证没有保留当前手机方式。");
        vault.CancelLoginChange=false;securitySettings.WindowsHelloEnabled=true;securitySettings.SecurityPassword="synthetic-password";securitySettings.SecurityCode="123456";
        await securitySettings.SaveCommand.ExecuteAsync(null);
        if(!vault.UsesWindowsHello || securitySettings.HasLoginMethodChanges || securitySettings.SecurityPassword!="" || securitySettings.SecurityCode!="") throw new Exception("底部保存设置未保存 Hello 或未清理凭证。");
        securitySettings.Reload();if(!securitySettings.WindowsHelloEnabled) throw new Exception("重新打开设置丢失已保存的 Hello。");
        vault.Lock();var changedLogin=new VaultViewModel(vault,settings,new Interaction());
        if(changedLogin.ShowTotp || changedLogin.AuthenticationHint.Contains("只验证手机")) throw new Exception("读取认证方式前误显示手机验证。");
        await changedLogin.InitializeAsync();
        if(!changedLogin.ShowHelloVerification || changedLogin.ShowTotp || !changedLogin.AuthenticationHint.Contains("Windows Hello")) throw new Exception("保存 Hello 后锁定仍显示手机验证。");
        vault.Lock();
        if(await VaultStartup.TryEnterAsync(vault,initial) || vault.Authentication!=AuthenticationState.Locked) throw new Exception("未验证手机便进入工作区。");
        // 首次 Hello 按钮等待恢复密钥保存确认；再次进入只显示所选方式且不显示路径/密码。
        var helloVault=new FakeVault {HelloSupported=true}; var helloSettings=new Settings();
        var firstHello=new VaultViewModel(helloVault,helloSettings,new Interaction());
        firstHello.Password="synthetic-hello-password"; await firstHello.CreateCommand.ExecuteAsync(null);
        if(!firstHello.ShowHelloChoice || firstHello.ChooseWindowsHelloCommand.CanExecute(null)) throw new Exception("Hello 首次入口未等待恢复密钥确认。");
        firstHello.RecoverySaved=true; await firstHello.ChooseWindowsHelloCommand.ExecuteAsync(null);
        helloVault.Lock(); var helloDaily=new VaultViewModel(helloVault,helloSettings,new Interaction());
        await helloDaily.InitializeAsync();
        if(helloDaily.ShowMaintenance || !helloDaily.ShowHelloVerification || helloDaily.ShowHelloChoice || helloDaily.ShowTotp || helloDaily.ShowPasswordEntry || helloDaily.ShowDirectoryEntry || helloDaily.VerifyCodeCommand.CanExecute(null))
            throw new Exception("日常 Hello 显示了 TOTP 降级入口或首次密码/路径。");
        var helloEntered=0; helloDaily.Ready+=()=>helloEntered++;
        await helloDaily.VerifyWindowsHelloCommand.ExecuteAsync(null);
        if(helloEntered!=1 || !helloVault.IsUnlocked || helloDaily.ReplacePhoneCommand.CanExecute(null)) throw new Exception("Hello 成功未进入工作区或仍启用手机维护。");
        var helloSecurity=new SettingsViewModel(new WorkspaceState(new FakeUseCases(),new Interaction()),helloSettings,helloVault);
        if(!helloSecurity.SecuritySettingsAvailable || !helloSecurity.WindowsHelloEnabled || helloSecurity.PhoneVerificationEnabled) throw new Exception("Hello 与手机选项没有互斥。");
        Console.WriteLine("保险库界面验证通过：恢复确认、凭证清理、目录绑定、日常 TOTP/Hello、首次选择与手机开关隔离。");
    }
    private sealed class FakeVault : IVaultService
    {
        public bool HelloSupported {get;set;}
        public bool UsesWindowsHello {get;private set;}
        public Task<bool> IsWindowsHelloAvailableAsync()=>Task.FromResult(HelloSupported);
        public bool CancelLoginChange {get;set;}
        public Task SetLoginMethodAsync(bool useHello,string password,string code) {if(CancelLoginChange) throw new OperationCanceledException();UsesWindowsHello=useHello;PhoneVerificationEnabled=!useHello;return Task.CompletedTask;}
        public bool CanChooseWindowsHello=>HelloSupported && Authentication==AuthenticationState.PendingEnrollment;
        public async Task EnrollWindowsHelloAsync(bool saved) {if(!saved) throw new CRS.Domain.CrsException("请保存恢复密钥。"); UsesWindowsHello=true; await CompleteEnrollmentAsync(true);}
        public Task VerifyWindowsHelloAsync()=>CompleteEnrollmentAsync(true);
        public bool IsUnlocked {get;private set;}
        // 假服务只提供容量元数据，不接触真实文件或密钥。
        public VaultBackupCapacity? BackupCapacity {get;set;}
        public bool PhoneVerificationEnabled {get;private set;}=true;
        public Task SetPhoneVerificationAsync(bool enabled,string password,string code) {PhoneVerificationEnabled=enabled; return Task.CompletedTask;}
        public AuthenticationState Authentication {get;private set;}=AuthenticationState.Locked;
        private bool created;
        public bool LocalAvailable {get;set;}
        public bool FailLocalUnlock {get;set;}
        public int LocalUnlocks {get;private set;}
        public int PasswordUnlocks {get;private set;}
        public VaultState Inspect(string directory)=>created?VaultState.Encrypted:VaultState.Missing;
        public bool HasLocalKey(string directory)=>created&&LocalAvailable;
        public Task BeginLocalUnlockAsync(string directory)
        {
            LocalUnlocks++;
            // 模拟读取过程中已产生受限上下文后失败，验证启动层会清理该上下文。
            if(FailLocalUnlock) {Authentication=AuthenticationState.PendingMfa; throw new InvalidOperationException("模拟本机凭证损坏。");}
            IsUnlocked=!PhoneVerificationEnabled && !UsesWindowsHello; Authentication=UsesWindowsHello?AuthenticationState.PendingWindowsHello:PhoneVerificationEnabled?AuthenticationState.PendingMfa:AuthenticationState.Authenticated; return Task.CompletedTask;
        }
        public Task<VaultCreationInfo> CreateAsync(string directory,string password) {created=true; Authentication=AuthenticationState.PendingEnrollment; return Task.FromResult(new VaultCreationInfo("synthetic-recovery-secret"));}
        public Task UnlockAsync(string directory,string password) {PasswordUnlocks++; Authentication=AuthenticationState.PendingMfa; return Task.CompletedTask;}
        public Task<EnrollmentImage> BeginEnrollmentAsync()=>Task.FromResult(new EnrollmentImage(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aPrsAAAAASUVORK5CYII=")));
        public Task<IReadOnlyList<string>> ConfirmEnrollmentAsync(string code) {Authentication=AuthenticationState.AwaitingRecoveryConfirmation; return Task.FromResult<IReadOnlyList<string>>(["synthetic-mfa-recovery"]);}
        public Task CompleteEnrollmentAsync(bool saved) {IsUnlocked=saved; LocalAvailable=saved; Authentication=AuthenticationState.Authenticated; return Task.CompletedTask;}
        public Task VerifyTotpAsync(string code) {IsUnlocked=true; Authentication=AuthenticationState.Authenticated; return Task.CompletedTask;}
        public Task RecoverMfaAsync(string code,string password)=>Task.CompletedTask;
        public Task BeginPhoneReplacementAsync(string password,string code)=>Task.CompletedTask;
        public Task ResetPasswordAsync(string directory,string secret,string password)=>Task.CompletedTask;
        public Task ChangePasswordAsync(string oldPassword,string newPassword,string code)=>Task.CompletedTask;
        public Task BackupAsync(string path,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task RestoreAsync(string path,string target,string secret,bool recovery,string password,CancellationToken cancellation=default)=>Task.CompletedTask;
        public void Lock() {IsUnlocked=false; Authentication=AuthenticationState.Locked;}
        public void Dispose()=>Lock();
    }
    private sealed class Settings : IDesktopSettingsService
    {
        public DesktopSettings Current {get;private set;}=new("D:/synthetic-vault","D:/synthetic-logs");
        public DesktopSettings Load()=>Current;
        public Task SaveAsync(DesktopSettings settings) {Current=settings; return Task.CompletedTask;}
    }
    private sealed class Interaction : IUserInteraction
    {
        public string[] PickFiles(string filter,bool multiple)=>[];
        public string? SaveFile(string filter,string name)=>null;
        public void ShowError(string message)=>throw new Exception(message);
        public void OpenFolder(string path){}
    }
}
