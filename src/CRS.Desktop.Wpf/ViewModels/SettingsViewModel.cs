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
    [ObservableProperty] private string securityMessage="手机验证默认启用；关闭后，本机依靠 Windows 用户保护的密钥进入。";
    public bool SecuritySettingsAvailable=>vault is {IsUnlocked:true};
    // 凭证仅用于本次开关验证，不进入 DesktopSettings 或日志。
    public string SecurityPassword {get;set;}="";
    public string SecurityCode {get;set;}="";
    public event Action? SecurityInputsCleared;
    [ObservableProperty] private string message = "目录和日志设置保存后下次启动生效；更换目录不会移动原数据库。";
    public string[] LogLevels { get; } = ["Debug", "Info", "Warn", "Error"];

    public SettingsViewModel(WorkspaceState state, IDesktopSettingsService service,IVaultService? vault=null)
    {
        State = state; this.service = service; this.vault=vault; Reload();
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) {SaveCommand.NotifyCanExecuteChanged(); SavePhoneVerificationCommand.NotifyCanExecuteChanged();} };
    }
    public void Reload()
    {
        var settings = service.Load();
        DatabaseDirectory = settings.DatabaseDirectory; LogDirectory = settings.LogDirectory;
        LoggingEnabled = settings.LoggingEnabled; LogLevel = settings.LogLevel;
        LogRetentionFiles = settings.LogRetentionFiles; LogFileSizeMb = settings.LogFileSizeMb;
        if(SecuritySettingsAvailable) PhoneVerificationEnabled=vault!.PhoneVerificationEnabled;
        OnPropertyChanged(nameof(SecuritySettingsAvailable)); ClearSecurityInputs();
        Message = "目录和日志设置保存后下次启动生效；更换目录不会移动原数据库。";
    }
    private bool CanSave() => State.IsIdle;
    private bool CanSavePhone()=>State.IsIdle && SecuritySettingsAvailable;
    /// <summary>登录开关保存在加密库，单独验证并即时生效，不与路径／日志设置混为一项。</summary>
    [RelayCommand(CanExecute=nameof(CanSavePhone))]
    private async Task SavePhoneVerificationAsync()
    {
        try
        {
            if(await State.RunAsync(()=>vault!.SetPhoneVerificationAsync(PhoneVerificationEnabled,SecurityPassword,SecurityCode)))
                SecurityMessage=PhoneVerificationEnabled?"手机验证已启用，下次进入或解除锁定需要手机代码。":"手机验证已关闭，下次本机进入不再要求手机代码。数据库仍保持加密。";
            else PhoneVerificationEnabled=vault!.PhoneVerificationEnabled;
        }
        finally {ClearSecurityInputs();}
    }
    public void ClearSecurityInputs() {SecurityPassword=""; SecurityCode=""; SecurityInputsCleared?.Invoke();}
    [RelayCommand]
    private void BrowseDatabase() { var path = State.Interaction.PickFolder(); if (path is not null) DatabaseDirectory = path; }
    [RelayCommand]
    private void BrowseLogs() { var path = State.Interaction.PickFolder(); if (path is not null) LogDirectory = path; }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var settings = new DesktopSettings(DatabaseDirectory, LogDirectory, LoggingEnabled, LogLevel, LogRetentionFiles, LogFileSizeMb);
        if (await State.RunAsync(() => service.SaveAsync(settings)))
        { Message = "目录和日志设置已保存，下次启动生效。当前任务和数据库保持不变。"; State.Status = Message; }
    }
}
