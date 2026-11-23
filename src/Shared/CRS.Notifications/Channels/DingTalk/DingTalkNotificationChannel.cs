// 钉钉通知渠道适配：读取对应Webhook配置并生成签名请求，沿用统一投递结果和凭据保护边界。
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace CRS.Notifications;

public sealed class DingTalkNotificationChannel(WebhookSettingsStore settings,HttpClient? client=null,TimeProvider? clock=null):WebhookNotificationChannel(settings,client)
{
    public override NotificationChannelDescriptor Descriptor {get;}=new("dingtalk","钉钉");
    private protected override HttpRequestMessage CreateRequest(string access,string signing,NotificationEnvelope message)
    {
        var timestamp=CRS.Timing.BeijingClock.Read(clock??TimeProvider.System).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hash=HMACSHA256.HashData(Encoding.UTF8.GetBytes(signing),Encoding.UTF8.GetBytes(timestamp+"\n"+signing));
        var signature=Uri.EscapeDataString(Convert.ToBase64String(hash));
        return new(HttpMethod.Post,"https://oapi.dingtalk.com/robot/send?access_token="+access+"&timestamp="+timestamp+"&sign="+signature)
        {Content=JsonContent.Create(new{msgtype="text",text=new{content=message.Title+"\n"+message.Body},at=new{isAtAll=false}})};
    }
    private protected override (bool Accepted,string Code,string? Receipt) ReadResponse(JsonElement root)
    {var code=Code(root,"errcode");return (code==0,code==0?"ProviderAccepted":"DingTalkRejected",null);}
}
