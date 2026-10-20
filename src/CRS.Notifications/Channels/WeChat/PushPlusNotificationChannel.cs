using System.Net.Http.Json;
using System.Text.Json;
namespace CRS.Notifications;

/// <summary>个人微信经PushPlus绑定服务接收；本组件不模拟登录微信，不接管微信密码。</summary>
public sealed class PushPlusNotificationChannel(WebhookSettingsStore settings,HttpClient? client=null):WebhookNotificationChannel(settings,client)
{
    public override NotificationChannelDescriptor Descriptor {get;}=new("wechat","个人微信（PushPlus）");
    private protected override HttpRequestMessage CreateRequest(string access,string signing,NotificationEnvelope message)=>
        new(HttpMethod.Post,"https://www.pushplus.plus/send"){Content=JsonContent.Create(new{token=access,title=message.Title,content=message.Body,template="txt",channel="wechat"})};
    private protected override (bool Accepted,string Code,string? Receipt) ReadResponse(JsonElement root)
    {
        var code=Code(root,"code");string? receipt=null;
        if(code==200&&root.TryGetProperty("data",out var value)&&value.ValueKind==JsonValueKind.String)
        {var text=value.GetString();if(text is {Length:>0 and <=128}&&text.All(char.IsAsciiLetterOrDigit))receipt=text;}
        return (code==200,code==200?"ProviderAccepted":"PushPlusRejected",receipt);
    }
}
