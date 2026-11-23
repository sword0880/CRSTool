namespace CRS.Notifications;

/// <summary>公共邮件设置的非秘密快照；宿主负责配置授权和受保护目录的ACL。</summary>
public sealed record EmailSettings(bool Enabled=false,string Host="",int Port=587,string Security="StartTls",string From="",
    string[]? Recipients=null,string UserName="",bool HasPassword=false,long Revision=0);
public sealed record EmailSettingsUpdate(EmailSettings Settings,string? Password=null,bool ClearPassword=false)
{
    public override string ToString()=>"EmailSettingsUpdate { Password=[已隐藏] }";
}
/// <summary>稳定消息标识供上层Outbox关联，本组件不解释业务风险或决定何时提醒。</summary>
public sealed record EmailMessage(string MessageId,string Subject,string Body);
public sealed record EmailSendResult(string State,string Code,string? MessageId=null);
/// <summary>可复用、可替换的传输端口；设置存储和业务Outbox不依赖SMTP实现。</summary>
public interface IEmailTransport
{
    Task<EmailSendResult> SendAsync(EmailSettings settings,string password,EmailMessage message,CancellationToken token);
}
