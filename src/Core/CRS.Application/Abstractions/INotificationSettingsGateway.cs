namespace CRS.Application.Abstractions;

/// <summary>系统邮件设置端口；应用层不依赖Worker、SMTP库或邮件密码的保存机制。</summary>
public interface INotificationSettingsGateway
{
    Task<NotificationWebhookSettings> LoadWebhookAsync(string channel,CancellationToken token)=>throw new NotSupportedException();
    Task<NotificationWebhookSettings> SaveWebhookAsync(NotificationWebhookEdit edit,CancellationToken token)=>throw new NotSupportedException();
    Task<NotificationSendResult> TestWebhookAsync(string channel,CancellationToken token)=>throw new NotSupportedException();
    Task<NotificationEmailSettings> LoadAsync(CancellationToken token);
    Task<NotificationEmailSettings> SaveAsync(NotificationEmailEdit edit,CancellationToken token);
    Task<NotificationSendResult> TestAsync(CancellationToken token);
}
public sealed record NotificationEmailSettings(bool Enabled,string Host,int Port,string Security,string From,string Recipients,
    string UserName,bool HasPassword,long Revision);
public sealed record NotificationEmailEdit(bool Enabled,string Host,int Port,string Security,string From,string Recipients,
    string UserName,string? Password,bool ClearPassword,long Revision)
{
    public override string ToString()=>"NotificationEmailEdit { Password=[已隐藏] }";
}
public sealed record NotificationSendResult(string State,string Code);
