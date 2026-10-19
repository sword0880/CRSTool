using System.Security.Cryptography;
using System.Text;
using CRS.Notifications;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using System.Text.Json;

/// <summary>公共通知与宿主授权隔离验收；只使用假的渠道与传输，绝不向真实邮箱、钉钉或微信发送。</summary>
internal static class NotificationChecks
{
    public static async Task Run()
    {
        void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS 公共通知："+label);}
        void Reject(Action action,string label){try{action();}catch(InvalidDataException){Console.WriteLine("PASS 公共通知："+label);return;}throw new Exception(label);}
        var root=Path.Combine(Path.GetTempPath(),"CRS.Notifications.Tests",Guid.NewGuid().ToString("N"));StorageSecurity.Prepare(root,true);
        var transport=new FakeTransport();var email=new EmailNotificationService(root,transport);var secret="synthetic-authorization-code-"+Guid.NewGuid().ToString("N");
        var settings=email.Save(new(new(true,"smtp.invalid",587,"StartTls","sender@example.invalid",["receiver@example.invalid"],"synthetic-user"),secret));
        Check(transport.Calls==0,"保存配置不发送测试邮件或业务通知");
        Check(settings.HasPassword&&!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"email-settings.dpapi"))).Contains(secret),"SMTP口令只写入受保护密文，不出现在配置快照");
        var reopened=new EmailNotificationService(root,transport);Check(reopened.Read().HasPassword,"重建公共组件后保留已保护的配置");
        var result=await reopened.TestAsync(Guid.NewGuid(),default);
        Check(result.State=="SmtpAccepted"&&transport.Calls==1&&transport.Password==secret,"显式测试使用已保存配置，受理结果不冒充送达");
        var kept=reopened.Save(new(settings with{From="changed@example.invalid"}));
        Check(kept.HasPassword,"留空保持已保存授权码");
        Reject(()=>reopened.Save(new(settings)),"旧修订不能覆盖另一个窗口的新设置");
        Reject(()=>reopened.Save(new(kept with{Security="None"})),"明文SMTP或不明确TLS方式被拒绝");
        Reject(()=>reopened.Save(new(kept with{Recipients=["target@example.invalid\r\nBcc: leak@example.invalid"]})),"收件地址换行注入被拒绝");
        var cleared=reopened.Save(new(kept with{Enabled=false},ClearPassword:true));Check(!cleared.HasPassword,"明确清除授权码并禁用渠道后不保留旧秘密");
        Check(!new EmailSettingsUpdate(settings,secret).ToString().Contains(secret),"更新对象的诊断字符串隐藏口令");
        var mailChannel=new EmailNotificationChannel(reopened);var ding=new FakeChannel("dingtalk");var wechat=new FakeChannel("wechat"){Fail=true};
        var dispatcher=new NotificationDispatcher([mailChannel,ding,wechat]);var message=new NotificationEnvelope("synthetic.event.v1@crs.local","测试风险事件","ETF份额变化不代表机构确认交易。","EtfOutflow");
        var disabled=await dispatcher.SendAsync("email","default",message,default);
        Check(disabled.State==NotificationDeliveryState.Disabled&&transport.Calls==1,"关闭的邮件渠道不尝试外发");
        var dingResult=await dispatcher.SendAsync("dingtalk","default",message,default);
        Check(dingResult.State==NotificationDeliveryState.Accepted&&ding.Last==message,"其它渠道复用完全相同的业务消息，不需要SMTP字段");
        var failed=await dispatcher.SendAsync("wechat","default",message,default);
        Check(failed.State==NotificationDeliveryState.OutcomeUnknown&&ding.Calls==1,"渠道适配器故障独立保留，且不自动切换其它渠道");
        var unknown=await dispatcher.SendAsync("not-installed","default",message,default);
        Check(unknown.State==NotificationDeliveryState.Unavailable&&transport.Calls==1&&ding.Calls==1,"未安装渠道不会偷偷改用邮件或其它渠道");
        Check((await dispatcher.StatusAsync("default",default)).Count==3,"单个渠道配置故障不抹去其它渠道状态");
        using var repository=new EncryptedTaskRepository(Path.Combine(root,"scheduler-config.db"),RandomNumberGenerator.GetBytes(32));
        await using var catalog=new BackgroundPluginCatalog();var gateway=new WorkerNotificationGateway(reopened,dispatcher);
        await using var runtime=new BackgroundRuntime(repository,catalog,notifications:gateway);
        var anonymous=new BackgroundCaller("synthetic-reader",false);var administrator=new BackgroundCaller("synthetic-admin",true);
        var read=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"GetEmailSettings");
        Check((await runtime.HandleAsync(read,anonymous,default)).Code=="Forbidden","普通公开查询身份不能读取系统邮件私有设置");
        var snapshot=await runtime.HandleAsync(read,administrator,default);
        Check(snapshot.Code=="Ok"&&!snapshot.Payload!.Contains(secret),"管理员快照不返回已保存口令");
        var save=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"SaveEmailSettings",Payload:JsonSerializer.Serialize(new WorkerEmailUpdate(new(false,"",Revision:cleared.Revision))));
        Check((await runtime.HandleAsync(save,administrator,default)).Code=="Ok"&&repository.FindCommand(save.RequestId,administrator.Sid) is null,"邮件秘密参数不进入持久任务命令或业务重放日志");
    }
    private sealed class FakeTransport:IEmailTransport
    {
        public int Calls;public string? Password;
        public Task<EmailSendResult> SendAsync(EmailSettings settings,string password,EmailMessage message,CancellationToken token)
        {Calls++;Password=password;return Task.FromResult(new EmailSendResult("SmtpAccepted","SyntheticAccepted",message.MessageId));}
    }
    private sealed class FakeChannel(string id):INotificationChannel
    {
        public NotificationChannelDescriptor Descriptor {get;}=new(id,"Synthetic "+id);public bool Fail;public int Calls;public NotificationEnvelope? Last;
        public Task<NotificationChannelStatus> StatusAsync(string profileId,CancellationToken token)=>Fail?throw new IOException("synthetic channel failure"):Task.FromResult(new NotificationChannelStatus(id,profileId,true,true,"Synthetic"));
        public Task<NotificationDeliveryResult> SendAsync(string profileId,NotificationEnvelope message,CancellationToken token)
        {Calls++;Last=message;if(Fail)throw new IOException("synthetic channel failure");return Task.FromResult(new NotificationDeliveryResult(id,NotificationDeliveryState.Accepted,"SyntheticAccepted",message.MessageId));}
    }
}
