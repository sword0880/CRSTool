using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Notifications;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

internal static class WebhookNotificationChecks
{
    public static async Task Run()
    {
        void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS Webhook通知："+label);}
        void Reject(Action action,string label){try{action();}catch(InvalidDataException){Check(true,label);return;}throw new Exception(label);}
        var root=Path.Combine(Path.GetTempPath(),"CRS.Webhook.Tests",Guid.NewGuid().ToString("N"));StorageSecurity.Prepare(root,true);
        var store=new WebhookSettingsStore(root);var access=new string('a',64);var signing="SEC"+new string('b',64);var push=new string('c',32);
        var handler=new Handler();using var client=new HttpClient(handler);
        var message=new NotificationEnvelope("synthetic.webhook.1","小财牛测试","这是隔离测试，不会向真实服务发送。","Test");
        var ding=store.Save(new("dingtalk",true,0,access,signing));var wechat=store.Save(new("wechat",true,0,push));
        Check(handler.Calls==0,"保存配置不会外发");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"dingtalk-settings.dpapi"))).Contains(access)
            &&!JsonSerializer.Serialize(ding).Contains(signing),"密钥加密保存，快照无明文");
        Check(new WebhookSettingsStore(root).Read("wechat")==wechat,"重启组件保留受保护配置");
        Reject(()=>store.Save(new("dingtalk",true,0)),"旧修订拒绝覆盖");
        Reject(()=>store.Save(new("dingtalk",true,ding.Revision,"https://attacker.invalid/secret")),"拒绝网址代替Token，不能任意配置HTTP目标");
        Reject(()=>store.Save(new("wechat",true,wechat.Revision,SigningSecret:signing)),"微信不接受钉钉密钥");
        Reject(()=>store.Save(new("dingtalk",true,ding.Revision,ClearSigningSecret:true)),"启用钉钉必须具备加签密钥");
        Check(!new WebhookSettingsUpdate("wechat",true,1,push).ToString().Contains(push),"更新诊断字符串隐藏凭据");
        var clock=new FixedClock();var channel=new DingTalkNotificationChannel(store,client,clock);
        handler.Json="{\"errcode\":0,\"errmsg\":\"ok\"}";
        var result=await channel.SendAsync("default",message,default);
        var stamp=clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var expected=Uri.EscapeDataString(Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signing),Encoding.UTF8.GetBytes(stamp+"\n"+signing))));
        Check(result.State==NotificationDeliveryState.Accepted&&result.ProviderReceipt is null,"钉钉仅记为受理，不伪造送达回执");
        Check(handler.Uri=="https://oapi.dingtalk.com/robot/send?access_token="+access+"&timestamp="+stamp+"&sign="+expected,"钉钉毫秒时间戳及HMAC签名正确");
        using(var payload=JsonDocument.Parse(handler.Body!))Check(payload.RootElement.GetProperty("text").GetProperty("content").GetString()==message.Title+"\n"+message.Body
            &&!payload.RootElement.GetProperty("at").GetProperty("isAtAll").GetBoolean(),"正文保持完整，默认不群体提及");
        var calls=handler.Calls;var limited=await channel.SendAsync("default",message,default);
        Check(limited.State==NotificationDeliveryState.RetryableFailure&&handler.Calls==calls,"本地限流在HTTP提交前拒绝");
        handler.Json="{\"code\":200,\"data\":\"receipt123\"}";
        var wx=new PushPlusNotificationChannel(store,client);var wxResult=await wx.SendAsync("default",message,default);
        using(var payload=JsonDocument.Parse(handler.Body!))Check(handler.Uri=="https://www.pushplus.plus/send"&&payload.RootElement.GetProperty("channel").GetString()=="wechat"
            &&payload.RootElement.GetProperty("template").GetString()=="txt"&&!payload.RootElement.TryGetProperty("topic",out _),"个人微信采用固定HTTPS地址、纯文本、仅当前绑定账户");
        Check(wxResult.State==NotificationDeliveryState.Accepted&&wxResult.ProviderReceipt=="receipt123","PushPlus保留真实流水号，受理不冒充送达");
        handler.Json="{\"code\":400,\"msg\":\"synthetic secret must never surface\"}";
        var rejected=await new PushPlusNotificationChannel(store,client).SendAsync("default",message,default);
        Check(rejected.State==NotificationDeliveryState.PermanentFailure&&!JsonSerializer.Serialize(rejected).Contains("synthetic secret"),"提供商明确拒绝，无异常正文泄露");
        handler.Json="not json";
        Check((await new PushPlusNotificationChannel(store,client).SendAsync("default",message,default)).State==NotificationDeliveryState.OutcomeUnknown,"提交后的无效响应保留结果未知");
        handler.Json="{\"code\":200}";handler.Status=HttpStatusCode.InternalServerError;
        Check((await new PushPlusNotificationChannel(store,client).SendAsync("default",message,default)).State==NotificationDeliveryState.OutcomeUnknown,"HTTP服务器故障不自动重发");
        handler.Status=HttpStatusCode.Found;
        Check((await new PushPlusNotificationChannel(store,client).SendAsync("default",message,default)).State==NotificationDeliveryState.OutcomeUnknown,"非成功HTTP响应不能误报受理");
        handler.Status=HttpStatusCode.TooManyRequests;
        Check((await new PushPlusNotificationChannel(store,client).SendAsync("default",message,default)).State==NotificationDeliveryState.RetryableFailure,"HTTP限流识别为明确拒绝");
        handler.Status=HttpStatusCode.OK;handler.Throw=true;
        Check((await new PushPlusNotificationChannel(store,client).SendAsync("default",message,default)).State==NotificationDeliveryState.OutcomeUnknown,"提交后连接中断结果未知");
        handler.Throw=false;calls=handler.Calls;
        Check((await wx.SendAsync("other",message,default)).State==NotificationDeliveryState.Unavailable&&handler.Calls==calls,"未知Profile不外发");
        Check((await wx.SendAsync("default",message with{Body=new string('中',2000)},default)).State==NotificationDeliveryState.PermanentFailure&&handler.Calls==calls,"按UTF8字节拒绝过长正文，不截断");
        using(var cancel=new CancellationTokenSource())
        {cancel.Cancel();try{await wx.SendAsync("default",message,cancel.Token);throw new Exception("取消未生效");}catch(OperationCanceledException){Check(handler.Calls==calls,"提交前取消不外发");}}
        var disabled=store.Save(new("wechat",false,wechat.Revision,ClearToken:true));
        Check(!disabled.HasToken&&(await new PushPlusNotificationChannel(store,client).SendAsync("default",message,default)).State==NotificationDeliveryState.Disabled&&handler.Calls==calls,"清除凭据并关闭后不外发");
        var email=new EmailNotificationService(root);var dispatcher=new NotificationDispatcher([new EmailNotificationChannel(email),channel,new PushPlusNotificationChannel(store,client)]);
        using var repository=new EncryptedTaskRepository(Path.Combine(root,"scheduler.db"),RandomNumberGenerator.GetBytes(32));
        await using var catalog=new BackgroundPluginCatalog();await using var runtime=new BackgroundRuntime(repository,catalog,notifications:new WorkerNotificationGateway(email,dispatcher,store));
        var reader=new BackgroundCaller("synthetic-reader",false);var admin=new BackgroundCaller("synthetic-admin",true);
        var read=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"GetWebhookSettings",Payload:"dingtalk");
        Check((await runtime.HandleAsync(read,reader,default)).Code=="Forbidden","普通身份不能读取渠道配置");
        var snapshot=await runtime.HandleAsync(read,admin,default);
        Check(snapshot.Code=="Ok"&&!snapshot.Payload!.Contains(access)&&!snapshot.Payload.Contains(signing),"管理员IPC快照无密钥");
        var save=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"SaveWebhookSettings",Payload:JsonSerializer.Serialize(new WorkerWebhookUpdate("wechat",false,disabled.Revision,push)));
        Check((await runtime.HandleAsync(save,admin,default)).Code=="Ok"&&repository.FindCommand(save.RequestId,admin.Sid) is null,"秘密配置不进入持久业务命令");
        Check((await runtime.HandleAsync(save with{RequestId=Guid.NewGuid()},reader,default)).Code=="Forbidden","普通身份不能保存渠道配置");
    }
    private sealed class Handler:HttpMessageHandler
    {
        public int Calls;public string? Uri,Body;public string Json="{}";public HttpStatusCode Status=HttpStatusCode.OK;public bool Throw;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Calls++;Uri=request.RequestUri!.AbsoluteUri;Body=await request.Content!.ReadAsStringAsync(token);if(Throw)throw new HttpRequestException("synthetic");return new(Status){Content=new StringContent(Json)};}
    }
    private sealed class FixedClock:TimeProvider{public override DateTimeOffset GetUtcNow()=>DateTimeOffset.FromUnixTimeMilliseconds(1600000000000);}
}
