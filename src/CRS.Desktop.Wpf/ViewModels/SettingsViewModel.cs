using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;

namespace CRS.DesktopClient.ViewModels;

/// <summary>设置窗口只收集选项，持久化和路径校验交给后台。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IDesktopSettingsService service;
    private readonly IVaultService? vault;
    public WorkspaceState State { get; }
    [ObservableProperty] private string databaseDirectory = "";
    [ObservableProperty] private string logDirectory = "";
    [ObservableProperty] private bool loggingEnabled;
    [ObservableProperty] private string logLevel = "Info";
    [ObservableProperty] private int logRetentionFiles;
    [ObservableProperty] private int logFileSizeMb;
    [ObservableProperty] private bool phoneVerificationEnabled=true;
    [ObservableProperty] private bool windowsHelloEnabled;
    [ObservableProperty] private bool windowsHelloAvailable;
    [ObservableProperty] private string securityMessage="手机验证与 Windows Hello 只能选择一种；未配置 Windows Hello 时只能使用手机验证。";
    public bool SecuritySettingsAvailable=>vault is {IsUnlocked:true};
    public bool ShowPhoneCode=>vault is {IsUnlocked:true,UsesWindowsHello:false};
    public event Action? PhoneEnrollmentRequired;
    private bool savedWindowsHelloEnabled;
    public bool HasLoginMethodChanges=>SecuritySettingsAvailable && WindowsHelloEnabled!=savedWindowsHelloEnabled;
    partial void OnPhoneVerificationEnabledChanged(bool value) => WindowsHelloEnabled=!value;
    partial void OnWindowsHelloEnabledChanged(bool value)
    {
        PhoneVerificationEnabled=!value;
        OnPropertyChanged(nameof(HasLoginMethodChanges));
        SecurityMessage=HasLoginMethodChanges
            ? $"尚未保存：已选择{(value?" Windows Hello":"手机验证")}。请填写确认凭证并点击“保存设置”；当前仍使用{(savedWindowsHelloEnabled?" Windows Hello":"手机验证")}。"
            : value?"当前使用 Windows Hello，进入及解除锁定需要 Windows 验证。":"当前使用手机验证，进入及解除锁定需要手机代码。";
    }
    public async Task CheckHelloAvailabilityAsync()
    {
        WindowsHelloAvailable=vault is not null && await vault.IsWindowsHelloAvailableAsync();
        if(!WindowsHelloAvailable) SecurityMessage="尚未配置 Windows Hello，只能使用手机验证。请先在 Windows 设置中配置 PIN、指纹或人脸。";
    }
    // 凭证仅用于本次开关验证，不进入 DesktopSettings 或日志。
    public string SecurityPassword {get;set;}="";
    public string SecurityCode {get;set;}="";
    public string DocumentPassword {get;set;}="";
    public string DocumentPasswordConfirmation {get;set;}="";
    public event Action? DocumentInputsCleared;
    [ObservableProperty] private string documentPasswordMessage="文档加密密码统一用于税务底稿、投资绩效归档及解密，在当前加密数据库内保存。旧文件仍需要其原密码，可在此切换。";
    [RelayCommand(CanExecute=nameof(CanSavePhone))]
    private async Task SaveDocumentPasswordAsync()
    {
        if(DocumentPassword.Length is <12 or >1024 || DocumentPassword!=DocumentPasswordConfirmation)
        {DocumentPasswordMessage="请填写 12—1024 个字符，并确保两次密码一致。";return;}
        try {DocumentPasswordMessage=await State.RunAsync(()=>vault!.SetDocumentEncryptionPasswordAsync(DocumentPassword))?"文档加密密码已保存，所有文档导出与解密立即使用此设置。":"文档加密密码未保存，请检查提示后重试。";}
        finally {DocumentPassword="";DocumentPasswordConfirmation="";DocumentInputsCleared?.Invoke();}
    }
    public event Action? SecurityInputsCleared;
    [ObservableProperty] private string message = "目录和日志设置保存后下次启动生效；更换目录不会移动原数据库。";
    public string[] LogLevels { get; } = ["Debug", "Info", "Warn", "Error"];

    public SettingsViewModel(WorkspaceState state, IDesktopSettingsService service,IVaultService? vault=null)
    {
        State = state; this.service = service; this.vault=vault; Reload();
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) {SaveCommand.NotifyCanExecuteChanged(); SavePhoneVerificationCommand.NotifyCanExecuteChanged();SaveDocumentPasswordCommand.NotifyCanExecuteChanged();} };
    }
    public void Reload()
    {
        var settings = service.Load();
        DatabaseDirectory = settings.DatabaseDirectory; LogDirectory = settings.LogDirectory;
        LoggingEnabled = settings.LoggingEnabled; LogLevel = settings.LogLevel;
        LogRetentionFiles = settings.LogRetentionFiles; LogFileSizeMb = settings.LogFileSizeMb;
        if(SecuritySettingsAvailable) {savedWindowsHelloEnabled=vault!.UsesWindowsHello; WindowsHelloEnabled=savedWindowsHelloEnabled;}
        OnPropertyChanged(nameof(HasLoginMethodChanges));
        SecurityMessage=WindowsHelloEnabled?"当前使用 Windows Hello，进入及解除锁定需要 Windows 验证。":"当前使用手机验证，进入及解除锁定需要手机代码。";
        OnPropertyChanged(nameof(SecuritySettingsAvailable)); ClearSecurityInputs();
        OnPropertyChanged(nameof(ShowPhoneCode));
        if(SecuritySettingsAvailable)DocumentPasswordMessage=vault!.DocumentEncryptionPassword.Length>0?"已配置统一文档加密密码。留空保持当前密码；旧文件仍使用其原密码。":"尚未配置文档加密密码，请在此统一设置。";
        Message = "目录和日志设置保存后下次启动生效；更换目录不会移动原数据库。";
    }
    private bool CanSave() => State.IsIdle;
    private bool CanSavePhone()=>State.IsIdle && SecuritySettingsAvailable;
    private bool ValidateSecurityInputs()
    {
        string? error=string.IsNullOrEmpty(SecurityPassword)?"请输入创建数据库时设置的主密码，以确认修改验证方式。"
            : ShowPhoneCode && (SecurityCode.Length!=6 || !SecurityCode.All(c=>c is >= '0' and <= '9'))
                ?"请输入当前手机上的新六位验证码，以确认切换验证方式。":null;
        if(error is null) return true;
        SecurityMessage=error;Message="验证方式尚未保存。";State.Status=error;return false;
    }
    /// <summary>登录开关保存在加密库，单独验证并即时生效，不与路径／日志设置混为一项。</summary>
    [RelayCommand(CanExecute=nameof(CanSavePhone))]
    private async Task SavePhoneVerificationAsync()=>await SaveVerificationAsync();
    private async Task<bool> SaveVerificationAsync()
    {
        if(!ValidateSecurityInputs()) return false;
        try
        {
            var rebind=vault!.UsesWindowsHello && !WindowsHelloEnabled;
            if(await State.RunAsync(()=>vault.SetLoginMethodAsync(WindowsHelloEnabled,SecurityPassword,SecurityCode)))
            {
                savedWindowsHelloEnabled=WindowsHelloEnabled;
                OnPropertyChanged(nameof(HasLoginMethodChanges));
                SecurityMessage=WindowsHelloEnabled?"已使用 Windows Hello 验证，下次进入或解锁需要 Windows Hello。":"已选择手机验证，下次进入或解锁需要手机代码。";
                OnPropertyChanged(nameof(ShowPhoneCode));
                if(rebind) PhoneEnrollmentRequired?.Invoke();
                return true;
            }
            else
            {
                WindowsHelloEnabled=savedWindowsHelloEnabled;
                SecurityMessage="验证方式未修改："+State.Status;
                return false;
            }
        }
        finally {ClearSecurityInputs();}
    }
    public void ClearSecurityInputs() {SecurityPassword=""; SecurityCode=""; DocumentPassword="";DocumentPasswordConfirmation="";SecurityInputsCleared?.Invoke();}
    [RelayCommand]
    private void BrowseDatabase() { var path = State.Interaction.PickFolder(); if (path is not null) DatabaseDirectory = path; }
    [RelayCommand]
    private void BrowseLogs() { var path = State.Interaction.PickFolder(); if (path is not null) LogDirectory = path; }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if(HasLoginMethodChanges && !ValidateSecurityInputs()) return;
        if(DocumentPassword.Length>0 || DocumentPasswordConfirmation.Length>0)
        {await SaveDocumentPasswordAsync();if(!DocumentPasswordMessage.Contains("已保存"))return;}
        var settings = new DesktopSettings(DatabaseDirectory, LogDirectory, LoggingEnabled, LogLevel, LogRetentionFiles, LogFileSizeMb);
        if (!await State.RunAsync(() => service.SaveAsync(settings))) return;
        if(HasLoginMethodChanges)
        {
            if(!await SaveVerificationAsync())
            {Message="目录和日志设置已保存；验证方式未修改，请重新选择并完成验证。";return;}
            Message="设置已保存，验证方式立即生效；目录和日志设置下次启动生效。";
        }
        else Message="目录和日志设置已保存，下次启动生效。当前验证方式保持不变。";
        State.Status=Message;
    }
}
