namespace CRS.Notifications;

/// <summary>邮件是公共多渠道通知的一个适配器；其它渠道不需要复用或理解SMTP设置。</summary>
public sealed class EmailNotificationChannel(EmailNotificationService email):INotificationChannel
{
    public NotificationChannelDescriptor Descriptor {get;}=new("email","邮件");
    public Task<NotificationChannelStatus> StatusAsync(string profileId,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(profileId!="default")return Task.FromResult(new NotificationChannelStatus("email",profileId,false,false,"ProfileNotFound"));
        var settings=email.Read();var configured=settings.Host.Length>0&&settings.From.Length>0&&settings.Recipients is {Length:>0}
            &&(settings.UserName.Length==0||settings.HasPassword);
        return Task.FromResult(new NotificationChannelStatus("email",profileId,settings.Enabled,configured,"ConfiguredStatus"));
    }
    public async Task<NotificationDeliveryResult> SendAsync(string profileId,NotificationEnvelope message,CancellationToken token)
    {
        if(profileId!="default")return new("email",NotificationDeliveryState.Unavailable,"ProfileNotFound",message.MessageId);
        EmailSendResult result;
        // 邮件的格式限制比公共消息更严格，明确在提交前拒绝时不能记为受理结果未知。
        try{result=await email.SendAsync(new(message.MessageId,message.Title,message.Body),token);}
        catch(InvalidDataException){return new("email",NotificationDeliveryState.PermanentFailure,"InvalidEmailConfigurationOrMessage",message.MessageId);}
        var state=result.State switch
        {
            "SmtpAccepted"=>NotificationDeliveryState.Accepted,"Disabled"=>NotificationDeliveryState.Disabled,
            "Retry"=>NotificationDeliveryState.RetryableFailure,"UnknownOutcome"=>NotificationDeliveryState.OutcomeUnknown,
            _=>NotificationDeliveryState.PermanentFailure
        };
        return new("email",state,result.Code,message.MessageId);
    }
}
