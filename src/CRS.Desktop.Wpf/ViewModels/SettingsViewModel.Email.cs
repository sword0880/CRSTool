using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using CRS.Application.Abstractions;
namespace CRS.DesktopClient.ViewModels;

/// <summary>系统邮件页仅管理表单；配置与发送经DesktopWorkflow交给公共通知组件。</summary>
public partial class SettingsViewModel
{
    private NotificationEmailSettings? savedEmail;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(SaveEmailCommand)),NotifyCanExecuteChangedFor(nameof(TestEmailCommand)),NotifyCanExecuteChangedFor(nameof(ReloadEmailCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditEmail))] private bool emailBusy;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(SaveEmailCommand)),NotifyCanExecuteChangedFor(nameof(TestEmailCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditEmail))] private bool emailAvailable;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestEmailCommand))] private bool emailEnabled;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestEmailCommand))] private string emailHost="";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestEmailCommand))] private int emailPort=587;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestEmailCommand))] private string emailSecurity="StartTls";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestEmailCommand))] private string emailFrom="";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestEmailCommand))] private string emailRecipients="";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestEmailCommand))] private string emailUserName="";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestEmailCommand))] private bool emailClearPassword;
    [ObservableProperty] private string emailPasswordStatus="尚未加载后台配置。";
    [ObservableProperty] private string emailMessage="正在读取系统邮件设置；需要本机后台和管理员权限。";
    private string emailPassword="";
    public event Action? EmailInputCleared;
    public bool CanEditEmail=>State.IsIdle&&!EmailBusy&&EmailAvailable;
    public bool HasEmailChanges=>savedEmail is {} x&&(EmailEnabled!=x.Enabled||EmailHost!=x.Host||EmailPort!=x.Port||EmailSecurity!=x.Security
        ||EmailFrom!=x.From||EmailRecipients!=x.Recipients||EmailUserName!=x.UserName||emailPassword.Length>0||EmailClearPassword);
    public void SetEmailPassword(string value){emailPassword=value;TestEmailCommand.NotifyCanExecuteChanged();}
    private bool CanLoadEmail()=>State.IsIdle&&!EmailBusy;
    private bool CanTestEmail()=>CanEditEmail&&!HasEmailChanges;
    private void NotifyEmailCommands()
    {
        OnPropertyChanged(nameof(CanEditEmail));SaveEmailCommand.NotifyCanExecuteChanged();TestEmailCommand.NotifyCanExecuteChanged();ReloadEmailCommand.NotifyCanExecuteChanged();
    }
    public async Task LoadEmailSettingsAsync()
    {
        if(!CanLoadEmail())return;EmailBusy=true;
        try{ApplyEmail(await State.UseCases.LoadEmailSettingsAsync());EmailAvailable=true;EmailMessage="邮件由后台公共通知组件发送。填写并保存配置后，可显式发送测试邮件。";}
        catch(Exception error){EmailAvailable=false;EmailMessage=error is InvalidDataException?error.Message:"无法读取邮件设置，请确认后台服务已配套更新。";}
        finally{EmailBusy=false;}
    }
    private void ApplyEmail(NotificationEmailSettings value)
    {
        savedEmail=value;EmailEnabled=value.Enabled;EmailHost=value.Host;EmailPort=value.Port;EmailSecurity=value.Security;
        EmailFrom=value.From;EmailRecipients=value.Recipients;EmailUserName=value.UserName;EmailClearPassword=false;
        EmailPasswordStatus=value.HasPassword?"已保存授权码；留空保持，勾选清除才会删除。":"尚未保存授权码。";ClearEmailInput();
    }
    private void ClearEmailInput(){emailPassword="";EmailInputCleared?.Invoke();TestEmailCommand.NotifyCanExecuteChanged();}
    [RelayCommand(CanExecute=nameof(CanLoadEmail))] private Task ReloadEmail()=>LoadEmailSettingsAsync();
    [RelayCommand(CanExecute=nameof(CanEditEmail))] private async Task SaveEmail()=>await SaveEmailCoreAsync();
    private async Task<bool> SaveEmailCoreAsync()
    {
        if(!CanEditEmail||savedEmail is null)return false;EmailBusy=true;
        try
        {
            var draft=new NotificationEmailEdit(EmailEnabled,EmailHost,EmailPort,EmailSecurity,EmailFrom,EmailRecipients,EmailUserName,emailPassword.Length==0?null:emailPassword,EmailClearPassword,savedEmail.Revision);
            ApplyEmail(await State.UseCases.SaveEmailSettingsAsync(draft));EmailMessage="系统邮件设置已保存，由后台生效；可发送测试邮件验证。";return true;
        }
        catch(Exception error){EmailMessage=error is InvalidDataException?error.Message:"保存结果未确认，请刷新邮件设置后核对；口令不会在提示中显示。";return false;}
        finally{ClearEmailInput();EmailBusy=false;}
    }
    [RelayCommand(CanExecute=nameof(CanTestEmail))] private async Task TestEmail()
    {
        EmailBusy=true;
        try
        {
            var result=await State.UseCases.TestEmailSettingsAsync();
            EmailMessage=result.State switch
            {
                "SmtpAccepted"=>"SMTP服务器已受理测试邮件，请检查收件箱；尚不能证明已送达或已读。",
                "Disabled"=>"邮件通知尚未启用，请先保存启用设置。",
                "UnknownOutcome"=>"提交后连接中断，受理结果未知，请先检查收件箱再决定是否重试。",
                _=>"测试邮件未成功受理；类别："+result.Code
            };
        }
        catch(Exception error){EmailMessage=error is InvalidDataException?error.Message:"测试结果未确认，请检查后台和收件箱。";}
        finally{EmailBusy=false;}
    }
}
