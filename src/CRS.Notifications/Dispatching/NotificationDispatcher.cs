namespace CRS.Notifications;
/// <summary>渠道注册表不包含调度和业务规则；明确指定渠道，不在失败时偷偷换渠道发送。</summary>
public sealed class NotificationDispatcher
{
    private readonly IReadOnlyDictionary<string,INotificationChannel> channels;
    public NotificationDispatcher(IEnumerable<INotificationChannel> adapters)
    {
        var list=adapters.ToArray();
        if(list.Any(x=>!ValidId(x.Descriptor.Id))||list.Select(x=>x.Descriptor.Id).Distinct(StringComparer.Ordinal).Count()!=list.Length)
            throw new ArgumentException("通知渠道标识为空或重复。");
        channels=list.ToDictionary(x=>x.Descriptor.Id,StringComparer.Ordinal);
    }
    public IReadOnlyList<NotificationChannelDescriptor> Descriptors=>channels.Values.Select(x=>x.Descriptor).ToArray();
    public async Task<IReadOnlyList<NotificationChannelStatus>> StatusAsync(string profileId,CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if(!ValidId(profileId))throw new ArgumentException("通知配置标识无效。",nameof(profileId));
        token.ThrowIfCancellationRequested();
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
        ArgumentNullException.ThrowIfNull(message);
        token.ThrowIfCancellationRequested();
        // 通道无关的输入在调用适配器之前拒绝，不能把明确未发送的无效内容误报为结果未知。
        if(!ValidId(channelId)||!ValidId(profileId)||!ValidText(message.MessageId,160,false)
            ||!ValidText(message.Title,200,false)||!ValidText(message.Body,32768,true)||!ValidText(message.Category,80,false)
            ||message.Attributes is {Count:>32}||message.Attributes?.Any(x=>!ValidText(x.Key,64,false)||!ValidText(x.Value,1024,true))==true)
            return new(channelId,NotificationDeliveryState.PermanentFailure,"InvalidNotification",message.MessageId);
        if(!channels.TryGetValue(channelId,out var channel))return new(channelId,NotificationDeliveryState.Unavailable,"ChannelNotInstalled",message.MessageId);
        // 冻结调用方的附加字段，避免排队期间业务修改字典改变已确定的同一条消息。
        var snapshot=message with{Attributes=message.Attributes is null?null:new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(message.Attributes.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal))};
        try
        {
            var result=await channel.SendAsync(profileId,snapshot,token);
            // 提供商适配器返回串单结果时不能记为已受理，也不静默重发。
            return result.ChannelId==channelId&&result.MessageId==message.MessageId?result:
                new(channelId,NotificationDeliveryState.OutcomeUnknown,"AdapterResultMismatch",message.MessageId);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        // 适配器异常不能证明提交没有发生，统一保守记为结果未知，交由Outbox保留待核实。
        catch(Exception){return new(channelId,NotificationDeliveryState.OutcomeUnknown,"ChannelAdapterFailure",message.MessageId);}
    }
    private static bool ValidId(string? value)=>value is {Length:>0 and <=40}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
    private static bool ValidText(string? value,int maximum,bool multiline)=>value is {Length:>0}&&value.Length<=maximum&&value.All(c=>!char.IsControl(c)||multiline&&c is '\r' or '\n' or '\t');
}
