using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;
using CRS.DesktopClient.Services;

namespace CRS.DesktopClient.ViewModels;

/// <summary>保险库界面只调用应用端口；密码不绑定普通文本控件、不进入设置或日志。</summary>
public partial class VaultViewModel(IVaultService vault,IDesktopSettingsService settings,IUserInteraction interaction) : ObservableObject
{
    [ObservableProperty] private string directory=settings.Load().DatabaseDirectory;
    [ObservableProperty] private string backupPath="";
    [ObservableProperty] private string targetDirectory="";
    [ObservableProperty] private string recoveryKey="";
    [ObservableProperty] private bool recoverySaved;
    [ObservableProperty] private bool useRecovery;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private System.Windows.Media.Imaging.BitmapSource? enrollmentQr;
    [ObservableProperty] private string mfaRecoveryCodes="";
    [ObservableProperty] private bool mfaRecoverySaved;
    [ObservableProperty] private string authenticationHint=vault.IsUnlocked?"当前已解锁，可创建加密备份、改密或更换手机。":"请输入主密码，再验证手机上的六位代码。";
    [ObservableProperty] private string status=StateMessage(vault.Inspect(settings.Load().DatabaseDirectory));
    // PasswordBox 将输入交给本会话，完成操作后主动清空，不做持久化。
    public string Password {get;set;}="";
    public string NewPassword {get;set;}="";
    public string Secret {get;set;}="";
    public string Code {get;set;}="";
    public string MfaRecoveryCode {get;set;}="";
    private string? unlockedDirectory=vault.IsUnlocked?settings.Load().DatabaseDirectory:null;
    public bool IsUnlocked=>vault.IsUnlocked;
    public bool IsIdle=>!IsBusy;
    public bool HasDatabaseRecovery=>RecoveryKey.Length>0;
    public bool HasMfaRecoveryCodes=>MfaRecoveryCodes.Length>0;
    public event Action? Ready;
    public event Action? LockRequested;
    public event Action? SessionRestricted;
    public event Action? SensitiveCleared;
    private bool CanOperate()=>!IsBusy;
    // 控件门槛对应服务端状态，不让未验证密码时的按钮误导用户。
    private bool CanEnroll()=>!IsBusy && Directory==unlockedDirectory && vault.Authentication==AuthenticationState.PendingEnrollment;
    private bool CanVerify()=>!IsBusy && Directory==unlockedDirectory && vault.Authentication is AuthenticationState.PendingEnrollment or AuthenticationState.PendingMfa;
    private bool CanRecoverPhone()=>!IsBusy && Directory==unlockedDirectory && vault.Authentication==AuthenticationState.PendingMfa;
    private bool CanAuthorized()=>!IsBusy && Directory==unlockedDirectory && vault.IsUnlocked;
    private bool CanContinue()=>!IsBusy && Directory==unlockedDirectory && (RecoveryKey.Length==0 || RecoverySaved)
        && (vault.IsUnlocked || (vault.Authentication==AuthenticationState.AwaitingRecoveryConfirmation && MfaRecoverySaved));
    partial void OnIsBusyChanged(bool value) {OnPropertyChanged(nameof(IsIdle)); Refresh();}
    partial void OnDirectoryChanged(string value) {Status=StateMessage(vault.Inspect(value)); Refresh();}
    private static string StateMessage(VaultState state)=>state switch
    {
        VaultState.PlaintextRejected=>"检测到未加密旧库，本版本拒绝读取和迁移。请另选新的空目录，旧库和附属文件会保持原样。",
        VaultState.Encrypted=>"已有加密保险库，请先验证主密码，再输入手机验证码；遗忘密码可用数据库恢复密钥重设并重新绑定。",
        VaultState.Invalid=>"当前目录格式未知或不完整，不会自动覆盖或重建。请检查资料或选择新的空目录恢复备份。",
        _=>"当前没有保险库，请明确创建新库或从加密备份恢复。主密码至少 12 个字符。"
    };
    partial void OnRecoverySavedChanged(bool value)=>ContinueCommand.NotifyCanExecuteChanged();
    partial void OnMfaRecoverySavedChanged(bool value)=>ContinueCommand.NotifyCanExecuteChanged();
    partial void OnRecoveryKeyChanged(string value)=>OnPropertyChanged(nameof(HasDatabaseRecovery));
    partial void OnMfaRecoveryCodesChanged(string value)=>OnPropertyChanged(nameof(HasMfaRecoveryCodes));
    private void Refresh()
    {
        CreateCommand.NotifyCanExecuteChanged(); UnlockCommand.NotifyCanExecuteChanged(); ChangePasswordCommand.NotifyCanExecuteChanged();
        ResetPasswordCommand.NotifyCanExecuteChanged(); BackupCommand.NotifyCanExecuteChanged(); RestoreCommand.NotifyCanExecuteChanged();
        LockCommand.NotifyCanExecuteChanged(); ContinueCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(IsUnlocked));
        BeginEnrollmentCommand.NotifyCanExecuteChanged(); VerifyCodeCommand.NotifyCanExecuteChanged(); RecoverPhoneCommand.NotifyCanExecuteChanged(); ReplacePhoneCommand.NotifyCanExecuteChanged();
    }
    private async Task Run(Func<Task> action)
    {
        IsBusy=true;
        try { await action(); }
        catch(Exception ex) { Status=ex is CrsException?ex.Message:"安全操作失败，请核对凭证、备份格式和文件权限。原文件未被覆盖。"; interaction.ShowError(Status); }
        finally { Password=""; NewPassword=""; Secret=""; Code=""; MfaRecoveryCode=""; IsBusy=false; }
    }
    private async Task SaveLocation(string path)=>await settings.SaveAsync(settings.Load() with {DatabaseDirectory=path});
    [RelayCommand(CanExecute=nameof(CanOperate))]
    private async Task CreateAsync()=>await Run(async()=>
    {
        RecoverySaved=false; var created=await vault.CreateAsync(Directory,Password); RecoveryKey=created.RecoveryKey;
        unlockedDirectory=Directory;
        Status="请离线保存恢复密钥并勾选确认。主密码与恢复密钥全部丢失后，无法恢复数据。";
        await ShowEnrollment();
        await SaveLocation(Directory);
    });
    [RelayCommand(CanExecute=nameof(CanOperate))]
    private async Task UnlockAsync()=>await Run(async()=>
    {
        ClearMfaDisplay(); await vault.UnlockAsync(Directory,Password); unlockedDirectory=Directory;
        if(vault.Authentication==AuthenticationState.PendingEnrollment) await ShowEnrollment();
        else {Status="主密码已验证，输入 Microsoft Authenticator 上的六位验证码。"; AuthenticationHint=Status;}
    });
    [RelayCommand(CanExecute=nameof(CanOperate))]
    private async Task ResetPasswordAsync()=>await Run(async()=> {await vault.ResetPasswordAsync(Directory,Secret,NewPassword); unlockedDirectory=Directory; ClearMfaDisplay(); await ShowEnrollment(); Status="主密码已重设，必须绑定新手机并保存新的恢复码。";});
    [RelayCommand(CanExecute=nameof(CanAuthorized))]
    private async Task ChangePasswordAsync()=>await Run(async()=> {await vault.ChangePasswordAsync(Password,NewPassword,Code); Status="主密码已修改，恢复密钥仍有效；旧备份仍使用备份时的凭证。";});
    [RelayCommand(CanExecute=nameof(CanAuthorized))]
    private async Task BackupAsync()=>await Run(async()=> {await vault.BackupAsync(BackupPath); Status="加密备份已验证并保存，可使用备份时主密码或恢复密钥恢复。";});
    [RelayCommand(CanExecute=nameof(CanOperate))]
    private async Task RestoreAsync()=>await Run(async()=> {await vault.RestoreAsync(BackupPath,TargetDirectory,Secret,UseRecovery,NewPassword); Directory=TargetDirectory; await SaveLocation(Directory); Status="备份已恢复，请用新密码及手机验证码解锁。旧备份会恢复当时的手机绑定、恢复码与使用记录；建议重新绑定。";});
    // 绑定素材只在安全窗口展示，验证完成或关闭窗口后主动清理。
    private async Task ShowEnrollment()
    {
        var material=await vault.BeginEnrollmentAsync();
        try
        {
            using var stream=new System.IO.MemoryStream(material.Png); var bitmap=new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource=stream; bitmap.EndInit(); bitmap.Freeze(); EnrollmentQr=bitmap;
            AuthenticationHint="打开 Microsoft Authenticator → 添加账户 → 其他账户 → 扫描二维码，再填写六位验证码。";
        }
        finally {System.Security.Cryptography.CryptographicOperations.ZeroMemory(material.Png);}
    }
    [RelayCommand(CanExecute=nameof(CanEnroll))]
    private async Task BeginEnrollmentAsync()=>await Run(ShowEnrollment);
    [RelayCommand(CanExecute=nameof(CanVerify))]
    private async Task VerifyCodeAsync()=>await Run(async()=>
    {
        if(Directory!=unlockedDirectory) throw new CrsException("目录已改变，请重新验证主密码。");
        if(vault.Authentication==AuthenticationState.PendingEnrollment)
        {
            MfaRecoveryCodes=string.Join(Environment.NewLine,await vault.ConfirmEnrollmentAsync(Code)); EnrollmentQr=null; MfaRecoverySaved=false;
            AuthenticationHint="手机已验证。请离线保存八条一次性恢复码，勾选确认后进入工作区。"; Status=AuthenticationHint;
        }
        else {await vault.VerifyTotpAsync(Code); Status="主密码与手机验证成功，可以进入工作区。";}
    });
    [RelayCommand(CanExecute=nameof(CanRecoverPhone))]
    private async Task RecoverPhoneAsync()=>await Run(async()=> {await vault.RecoverMfaAsync(MfaRecoveryCode); ClearMfaDisplay(); await ShowEnrollment(); Status="手机恢复码已消费，只能绑定新手机，当前不能访问业务资料。";});
    [RelayCommand(CanExecute=nameof(CanAuthorized))]
    private async Task ReplacePhoneAsync()=>await Run(async()=> {await vault.BeginPhoneReplacementAsync(Password,Code); SessionRestricted?.Invoke(); unlockedDirectory=Directory; ClearMfaDisplay(); await ShowEnrollment(); Status="请扫描新手机；首码验证失败或取消时旧绑定仍可用。";});
    private void ClearMfaDisplay() {EnrollmentQr=null; MfaRecoveryCodes=""; MfaRecoverySaved=false;}
    public void ClearSensitiveDisplay() {ClearMfaDisplay(); RecoveryKey=""; Password=""; Secret=""; NewPassword=""; Code=""; MfaRecoveryCode=""; SensitiveCleared?.Invoke();}
    /// <summary>受限认证到期后清空二维码和恢复素材，不让窗口继续显示过期绑定秘密。</summary>
    public void CheckAuthenticationExpiry()
    {
        if(IsBusy || unlockedDirectory is null || vault.IsUnlocked) return;
        if(vault.Authentication==AuthenticationState.Locked)
        {
            unlockedDirectory=null; ClearSensitiveDisplay(); Status="认证已过期，请重新验证主密码。"; AuthenticationHint=Status; Refresh();
        }
    }
    [RelayCommand(CanExecute=nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        // 位置保存失败时不能进入使用错误配置的工作区；确认成功后再关闭向导。
        IsBusy=true;
        try
        {
            await SaveLocation(unlockedDirectory!);
            if(vault.Authentication==AuthenticationState.AwaitingRecoveryConfirmation) await vault.CompleteEnrollmentAsync(MfaRecoverySaved && (RecoveryKey.Length==0 || RecoverySaved));
            if(!vault.IsUnlocked) throw new CrsException("手机认证未完成。");
            IsBusy=false; ClearSensitiveDisplay(); RecoverySaved=false; Ready?.Invoke();
        }
        catch(Exception ex) {IsBusy=false; Status=ex is CrsException?ex.Message:"无法保存保险库位置，请检查本机设置目录权限。"; interaction.ShowError(Status);}
    }
    [RelayCommand(CanExecute=nameof(CanOperate))]
    private void Lock() {vault.Lock(); unlockedDirectory=null; ClearSensitiveDisplay(); Refresh(); LockRequested?.Invoke();}
}
