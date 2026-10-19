namespace CRS.Notifications;

/// <summary>业务无关的通知内容；不包含SMTP字段、Webhook地址、口令或具体收件人。</summary>
public sealed record NotificationEnvelope(string MessageId,string Title,string Body,string Category,
    IReadOnlyDictionary<string,string>? Attributes=null);
public enum NotificationDeliveryState { Disabled,Accepted,RetryableFailure,PermanentFailure,OutcomeUnknown,Unavailable }
/// <summary>受理与送达分开；只有渠道明确返回的编号才可填ProviderReceipt，不把本地消息ID充当送达凭证。</summary>
public sealed record NotificationDeliveryResult(string ChannelId,NotificationDeliveryState State,string Code,string MessageId,string? ProviderReceipt=null);
public sealed record NotificationChannelDescriptor(string Id,string Name,bool SupportsDeliveryReceipt=false,bool SupportsProviderIdempotency=false);
public sealed record NotificationChannelStatus(string ChannelId,string ProfileId,bool Enabled,bool Configured,string Code);

/// <summary>邮件、钉钉、微信等分别实现此端口，独立处理凭据、目标、模板、限流和提供商错误映射。</summary>
public interface INotificationChannel
{
    NotificationChannelDescriptor Descriptor {get;}
    Task<NotificationChannelStatus> StatusAsync(string profileId,CancellationToken token);
    Task<NotificationDeliveryResult> SendAsync(string profileId,NotificationEnvelope message,CancellationToken token);
}

/// <summary>渠道注册表不包含调度和业务规则；明确指定渠道，不在失败时偷偷换渠道发送。</summary>
public sealed class NotificationDispatcher
{
    private readonly IReadOnlyDictionary<string,INotificationChannel> channels;
    public NotificationDispatcher(IEnumerable<INotificationChannel> adapters)
    {
        var list=adapters.ToArray();
        if(list.Any(x=>string.IsNullOrWhiteSpace(x.Descriptor.Id))||list.Select(x=>x.Descriptor.Id).Distinct(StringComparer.Ordinal).Count()!=list.Length)
            throw new ArgumentException("通知渠道标识为空或重复。");
        channels=list.ToDictionary(x=>x.Descriptor.Id,StringComparer.Ordinal);
    }
    public IReadOnlyList<NotificationChannelDescriptor> Descriptors=>channels.Values.Select(x=>x.Descriptor).ToArray();
    public async Task<IReadOnlyList<NotificationChannelStatus>> StatusAsync(string profileId,CancellationToken token)
    {
        var result=new List<NotificationChannelStatus>();
        foreach(var channel in channels.Values)
        {
            try{result.Add(await channel.StatusAsync(profileId,token));}
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception){result.Add(new(channel.Descriptor.Id,profileId,false,false,"ConfigurationUnavailable"));}
        }
        return result;
    }
    public async Task<NotificationDeliveryResult> SendAsync(string channelId,string profileId,NotificationEnvelope message,CancellationToken token)
    {
        if(!channels.TryGetValue(channelId,out var channel))return new(channelId,NotificationDeliveryState.Unavailable,"ChannelNotInstalled",message.MessageId);
        try{return await channel.SendAsync(profileId,message,token);}
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        // 适配器异常不能证明提交没有发生，统一保守记为结果未知，交由Outbox保留待核实。
        catch(Exception){return new(channelId,NotificationDeliveryState.OutcomeUnknown,"ChannelAdapterFailure",message.MessageId);}
    }
}
