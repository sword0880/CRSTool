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
            || daily.IsUnlocked || vault.LocalUnlocks!=1 || vault.PasswordUnlocks!=0) throw new Exception("已有本机凭证仍要求创建／输入主密码，或未经手机验证已经进入。");
        var dailyEntered=0; daily.Ready+=()=>dailyEntered++;
        daily.ShowDirectoryOptions=true; if(!daily.ShowDirectoryEntry) throw new Exception("主动恢复／切换位置时无法查看目录。");
        daily.ShowDirectoryOptions=false;
        daily.Code="123456"; await daily.VerifyCodeCommand.ExecuteAsync(null); if(!vault.IsUnlocked || dailyEntered!=1 || daily.Code!="") throw new Exception("仅手机验证码日常登录未直接进入或未清理验证码。");
        vault.Lock(); vault.LocalAvailable=false;
        var recovery=new VaultViewModel(vault,settings,new Interaction()); await recovery.InitializeAsync();
        if(!recovery.ShowPasswordEntry || recovery.IsNewVault || vault.LocalUnlocks!=1) throw new Exception("本机凭证丢失时未提供原主密码恢复或重新要求创建库。");
        vault.LocalAvailable=true;
        await vault.VerifyTotpAsync("123456"); await vault.SetPhoneVerificationAsync(false,"synthetic-password","123456"); vault.Lock();
        // 实际启动入口在创建任何验证窗口前放行；异常本机凭证必须清理并回到恢复入口。
        if(!await VaultStartup.TryEnterAsync(vault,initial) || !vault.IsUnlocked) throw new Exception("关闭手机验证仍需创建验证窗口。");
        vault.Lock(); vault.FailLocalUnlock=true;
        if(await VaultStartup.TryEnterAsync(vault,initial) || vault.IsUnlocked || vault.Authentication!=AuthenticationState.Locked) throw new Exception("损坏本机凭证仍自动进入或残留认证上下文。");
        vault.FailLocalUnlock=false; vault.LocalAvailable=false;
        if(await VaultStartup.TryEnterAsync(vault,initial)) throw new Exception("关闭手机验证后缺失本机凭证仍能进入。");
        vault.LocalAvailable=true;
        var automatic=new VaultViewModel(vault,settings,new Interaction()); var automaticEntered=0; automatic.Ready+=()=>automaticEntered++;
        await automatic.InitializeAsync(); if(!vault.IsUnlocked || automaticEntered!=1 || automatic.ShowDirectoryEntry) throw new Exception("关闭手机验证后未自动进入或暴露了数据库位置。");
        // 设置开关走安全端口，独立于普通目录／日志保存，并在操作后清空确认凭证。
        var state=new WorkspaceState(new FakeUseCases(),new Interaction()); var securitySettings=new SettingsViewModel(state,settings,vault);
        if(!securitySettings.SecuritySettingsAvailable || securitySettings.PhoneVerificationEnabled) throw new Exception("设置没有读取当前库的关闭状态。");
        state.IsBusy=true; if(securitySettings.SavePhoneVerificationCommand.CanExecute(null)) throw new Exception("后台忙碌仍能改变登录开关。"); state.IsBusy=false;
        securitySettings.PhoneVerificationEnabled=true; securitySettings.SecurityPassword="synthetic-password"; securitySettings.SecurityCode="123456";
        await securitySettings.SavePhoneVerificationCommand.ExecuteAsync(null);
        if(!vault.PhoneVerificationEnabled || securitySettings.SecurityPassword!="" || securitySettings.SecurityCode!="") throw new Exception("设置没有保存开关或保留了确认凭证。");
        vault.Lock();
        if(await VaultStartup.TryEnterAsync(vault,initial) || vault.Authentication!=AuthenticationState.Locked) throw new Exception("重新启用手机验证后仍跳过验证窗口或残留预检会话。");
        Console.WriteLine("保险库界面验证通过：恢复确认、密码清理、目录与解锁绑定、进入后清理、锁定门槛（5 项）。");
    }
    private sealed class FakeVault : IVaultService
    {
        public bool IsUnlocked {get;private set;}
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
            IsUnlocked=!PhoneVerificationEnabled; Authentication=PhoneVerificationEnabled?AuthenticationState.PendingMfa:AuthenticationState.Authenticated; return Task.CompletedTask;
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
        public Task BackupAsync(string path)=>Task.CompletedTask;
        public Task RestoreAsync(string path,string target,string secret,bool recovery,string password)=>Task.CompletedTask;
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
