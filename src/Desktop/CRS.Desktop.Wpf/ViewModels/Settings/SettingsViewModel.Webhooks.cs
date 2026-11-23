using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;
namespace CRS.DesktopClient.ViewModels;

/// <summary>渠道表单只保留草稿；凭据保护、参数校验与HTTP发送由后台完成。</summary>
public sealed partial class WebhookSettingsViewModel(IDesktopUseCases useCases,string channel,string name):ObservableObject
{
    private NotificationWebhookSettings? saved;
    private string token="",signing="";
    public string Name {get;}=name;
    public bool IsDingTalk=>channel=="dingtalk";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(SaveCommand)),NotifyCanExecuteChangedFor(nameof(ReloadCommand)),NotifyCanExecuteChangedFor(nameof(TestCommand)),NotifyPropertyChangedFor(nameof(CanEdit))] private bool busy;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(SaveCommand)),NotifyCanExecuteChangedFor(nameof(TestCommand)),NotifyPropertyChangedFor(nameof(CanEdit))] private bool available;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestCommand))] private bool enabled;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestCommand))] private bool clearToken;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(TestCommand))] private bool clearSigningSecret;
    [ObservableProperty] private string message="需要管理员权限和配套后台服务。";
    [ObservableProperty] private string credentialStatus="尚未加载。";
    public bool CanEdit=>Available&&!Busy;
    public bool HasChanges=>saved is {} x&&(Enabled!=x.Enabled||token.Length>0||signing.Length>0||ClearToken||ClearSigningSecret);
    public event Action? InputsCleared;
    public void SetToken(string value){token=value;TestCommand.NotifyCanExecuteChanged();}
    public void SetSigningSecret(string value){signing=value;TestCommand.NotifyCanExecuteChanged();}
    public void ClearInputs(){token="";signing="";InputsCleared?.Invoke();TestCommand.NotifyCanExecuteChanged();}
    private bool CanReload()=>!Busy;
    private bool CanTest()=>CanEdit&&Enabled&&!HasChanges;
    public async Task LoadAsync()
    {
        if(Busy)return;Busy=true;
        try{Apply(await useCases.LoadWebhookSettingsAsync(channel));Available=true;Message="保存不会发送消息；测试使用已保存的启用配置。";}
        catch(Exception){Available=false;Message="无法读取渠道设置，请检查管理员权限及配套后台服务。";}
        finally{Busy=false;}
    }
    private void Apply(NotificationWebhookSettings value)
    {
        saved=value;Enabled=value.Enabled;ClearToken=false;ClearSigningSecret=false;
        CredentialStatus=$"Token：{(value.HasToken?"已保存":"未设置")}"+(IsDingTalk?$"；加签密钥：{(value.HasSigningSecret?"已保存":"未设置")}":"");ClearInputs();
    }
    public async Task<bool> SaveCoreAsync()
    {
        if(!CanEdit||saved is null)return false;Busy=true;
        try{Apply(await useCases.SaveWebhookSettingsAsync(new(channel,Enabled,saved.Revision,token,signing,ClearToken,ClearSigningSecret)));Message="已保存；可显式发送测试消息。";return true;}
        catch(Exception){Message="保存结果未确认，请刷新核对；启用前需填写Token，钉钉还需加签密钥。";return false;}
        finally{ClearInputs();Busy=false;}
    }
    [RelayCommand(CanExecute=nameof(CanReload))] private Task Reload()=>LoadAsync();
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task Save()=>await SaveCoreAsync();
    [RelayCommand(CanExecute=nameof(CanTest))] private async Task Test()
    {
        Busy=true;
        try
        {
            var result=await useCases.TestWebhookSettingsAsync(channel);
            Message=result.State switch{"Accepted"=>"提供商已受理，请到微信或钉钉核对；尚未确认送达。","OutcomeUnknown"=>"提交结果未知，请先核对接收端，再决定是否重试。",_=>"未受理；类别："+result.Code};
        }
        catch(Exception){Message="测试结果未确认，请检查后台和接收端。";}
        finally{Busy=false;}
    }
}
public partial class SettingsViewModel
{
    public WebhookSettingsViewModel DingTalk {get;private set;}=null!;
    public WebhookSettingsViewModel WeChat {get;private set;}=null!;
    private void InitializeWebhooks()
    {
        DingTalk=new(State.UseCases,"dingtalk","钉钉");WeChat=new(State.UseCases,"wechat","个人微信");
        DingTalk.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(DingTalk.Busy))SaveCommand.NotifyCanExecuteChanged();};
        WeChat.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(WeChat.Busy))SaveCommand.NotifyCanExecuteChanged();};
    }
    public async Task LoadWebhookSettingsAsync(){await DingTalk.LoadAsync();await WeChat.LoadAsync();}
}
