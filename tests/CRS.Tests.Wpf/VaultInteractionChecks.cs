using CRS.Application.Abstractions;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class VaultInteractionChecks
{
    /// <summary>离线恢复确认不能绕过，切换目录不能沿用解锁，密码操作完成后不保留输入。</summary>
    public static async Task RunAsync()
    {
        var vault=new FakeVault(); var settings=new Settings(); var model=new VaultViewModel(vault,settings,new Interaction()); var ready=0;
        model.Ready+=()=>ready++; model.Password="Synthetic-New-Password";
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
        Console.WriteLine("保险库界面验证通过：恢复确认、密码清理、目录与解锁绑定、进入后清理、锁定门槛（5 项）。");
    }
    private sealed class FakeVault : IVaultService
    {
        public bool IsUnlocked {get;private set;}
        public AuthenticationState Authentication {get;private set;}=AuthenticationState.Locked;
        public VaultState Inspect(string directory)=>VaultState.Missing;
        public Task<VaultCreationInfo> CreateAsync(string directory,string password) {Authentication=AuthenticationState.PendingEnrollment; return Task.FromResult(new VaultCreationInfo("synthetic-recovery-secret"));}
        public Task UnlockAsync(string directory,string password) {Authentication=AuthenticationState.PendingMfa; return Task.CompletedTask;}
        public Task<EnrollmentImage> BeginEnrollmentAsync()=>Task.FromResult(new EnrollmentImage(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aPrsAAAAASUVORK5CYII=")));
        public Task<IReadOnlyList<string>> ConfirmEnrollmentAsync(string code) {Authentication=AuthenticationState.AwaitingRecoveryConfirmation; return Task.FromResult<IReadOnlyList<string>>(["synthetic-mfa-recovery"]);}
        public Task CompleteEnrollmentAsync(bool saved) {IsUnlocked=saved; Authentication=AuthenticationState.Authenticated; return Task.CompletedTask;}
        public Task VerifyTotpAsync(string code) {IsUnlocked=true; Authentication=AuthenticationState.Authenticated; return Task.CompletedTask;}
        public Task RecoverMfaAsync(string code)=>Task.CompletedTask;
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
