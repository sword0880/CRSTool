using CRS.Notifications;
using CRS.Worker.Abstractions;
namespace CRS.Worker.Service;

/// <summary>宿主薄适配：只转换受限契约，SMTP、凭据保护与地址校验归公共通知组件。</summary>
public sealed class WorkerNotificationGateway(EmailNotificationService email,NotificationDispatcher dispatcher):IBackgroundNotificationContext
{
    public WorkerEmailSettings Read()=>Map(email.Read());
    public WorkerEmailSettings Save(WorkerEmailUpdate update)
    {
        var x=update.Settings??throw new InvalidDataException("邮件配置为空。");
        return Map(email.Save(new(new(x.Enabled,x.Host,x.Port,x.Security,x.From,x.Recipients,x.UserName,x.HasPassword,x.Revision),update.Password,update.ClearPassword)));
    }
    public async Task<BackgroundMailResult> TestAsync(Guid requestId,CancellationToken token)=>Map(await email.TestAsync(requestId,token));
    public async Task<BackgroundNotificationResult> SendNotificationAsync(string channelId,string profileId,BackgroundNotification message,CancellationToken token)
    {
        var result=await dispatcher.SendAsync(channelId,profileId,new(message.MessageId,message.Title,message.Body,message.Category,message.Attributes),token);
        return new(result.ChannelId,result.State.ToString(),result.Code,result.MessageId,result.ProviderReceipt);
    }
    public async Task<IReadOnlyList<BackgroundNotificationChannel>> NotificationChannelsAsync(CancellationToken token)
    {
        var status=await dispatcher.StatusAsync("default",token);
        return dispatcher.Descriptors.Select(x=>new BackgroundNotificationChannel(x.Id,x.Name,status.Any(s=>s.ChannelId==x.Id&&s.Enabled),status.Any(s=>s.ChannelId==x.Id&&s.Configured))).ToArray();
    }
    private static WorkerEmailSettings Map(EmailSettings x)=>new(x.Enabled,x.Host,x.Port,x.Security,x.From,x.Recipients,x.UserName,x.HasPassword,x.Revision);
    private static BackgroundMailResult Map(EmailSendResult x)=>new(x.State,x.Code,x.MessageId);
}
