namespace CRS.Notifications;

/// <summary>业务无关的通知内容；不包含SMTP字段、Webhook地址、口令或具体收件人。重试沿用MessageId。</summary>
public sealed record NotificationEnvelope(string MessageId,string Title,string Body,string Category,
    IReadOnlyDictionary<string,string>? Attributes=null);
public enum NotificationDeliveryState { Disabled,Accepted,RetryableFailure,PermanentFailure,OutcomeUnknown,Unavailable }
/// <summary>受理与送达分开；只有渠道明确返回的编号才可填ProviderReceipt，不把本地消息ID充当送达凭证。</summary>
public sealed record NotificationDeliveryResult(string ChannelId,NotificationDeliveryState State,string Code,string MessageId,string? ProviderReceipt=null);
public sealed record NotificationChannelDescriptor(string Id,string Name,bool SupportsDeliveryReceipt=false,bool SupportsProviderIdempotency=false);
public sealed record NotificationChannelStatus(string ChannelId,string ProfileId,bool Enabled,bool Configured,string Code);

/// <summary>
/// 邮件、钉钉、微信等分别实现此端口，独立处理凭据、目标、模板、限流和提供商错误映射。
/// profileId是受保护配置的标识，不是收件地址或Webhook；适配器必须检查启用状态。
/// 只有能证明未受理的暂时失败才返回RetryableFailure；提交后无法确认应返回OutcomeUnknown。
/// Accepted仅表示提供商受理，能力标志不代表本系统已实现送达查询或恰好一次投递。
/// </summary>
public interface INotificationChannel
{
    NotificationChannelDescriptor Descriptor {get;}
    Task<NotificationChannelStatus> StatusAsync(string profileId,CancellationToken token);
    Task<NotificationDeliveryResult> SendAsync(string profileId,NotificationEnvelope message,CancellationToken token);
}

