namespace CRS.Notifications;

/// <summary>邮件是公共多渠道通知的一个适配器；其它渠道不需要复用或理解SMTP设置。</summary>
public sealed class EmailNotificationChannel(EmailNotificationService email):INotificationChannel
{
    public NotificationChannelDescriptor Descriptor {get;}=new("email","邮件");
    public Task<NotificationChannelStatus> StatusAsync(string profileId,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(profileId!="default")return Task.FromResult(new NotificationChannelStatus("email",profileId,false,false,"ProfileNotFound"));
        var settings=email.Read();return Task.FromResult(new NotificationChannelStatus("email",profileId,settings.Enabled,settings.Host.Length>0,"ConfiguredStatus"));
    }
    public async Task<NotificationDeliveryResult> SendAsync(string profileId,NotificationEnvelope message,CancellationToken token)
    {
        if(profileId!="default")return new("email",NotificationDeliveryState.Unavailable,"ProfileNotFound",message.MessageId);
        var result=await email.SendAsync(new(message.MessageId,message.Title,message.Body),token);
        var state=result.State switch
        {
            "SmtpAccepted"=>NotificationDeliveryState.Accepted,"Disabled"=>NotificationDeliveryState.Disabled,
            "Retry"=>NotificationDeliveryState.RetryableFailure,"UnknownOutcome"=>NotificationDeliveryState.OutcomeUnknown,
            _=>NotificationDeliveryState.PermanentFailure
        };
        return new("email",state,result.Code,message.MessageId);
    }
}
