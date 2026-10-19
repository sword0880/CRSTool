namespace CRS.Worker.Abstractions;

/// <summary>仅返回SMTP非秘密设置及是否已有口令，绝不把已保存口令送回桌面或插件。</summary>
public sealed record WorkerEmailSettings(bool Enabled=false,string Host="",int Port=587,string Security="StartTls",string From="",
    string[]? Recipients=null,string UserName="",bool HasPassword=false,long Revision=0);
/// <summary>空口令表示保留现有值；清除必须明确指定，修订号防止旧窗口覆盖。</summary>
public sealed record WorkerEmailUpdate(WorkerEmailSettings Settings,string? Password=null,bool ClearPassword=false)
{
    public override string ToString()=>"WorkerEmailUpdate { Password=[已隐藏] }";
}
/// <summary>SmtpAccepted只表示服务器受理，不证明到达收件箱或用户已读。</summary>
public sealed record BackgroundMailResult(string State,string Code,string? MessageId=null);
/// <summary>业务插件只传递渠道中立的内容与配置标识，不获得各提供商的凭据或收件目标。</summary>
public sealed record BackgroundNotification(string MessageId,string Title,string Body,string Category,IReadOnlyDictionary<string,string>? Attributes=null);
public sealed record BackgroundNotificationResult(string ChannelId,string State,string Code,string MessageId,string? ProviderReceipt=null);
public sealed record BackgroundNotificationChannel(string Id,string Name,bool Enabled,bool Configured);
public interface IBackgroundNotificationContext
{
    Task<IReadOnlyList<BackgroundNotificationChannel>> NotificationChannelsAsync(CancellationToken token);
    Task<BackgroundNotificationResult> SendNotificationAsync(string channelId,string profileId,BackgroundNotification message,CancellationToken token);
}
