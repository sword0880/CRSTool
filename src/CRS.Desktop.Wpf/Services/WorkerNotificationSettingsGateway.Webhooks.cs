// 桌面Webhook设置的后台网关，转换应用与Worker合同，读取仅暴露凭据存在状态。
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.Worker.Abstractions;
namespace CRS.DesktopClient.Services;
public sealed partial class WorkerNotificationSettingsGateway
{
    public async Task<NotificationWebhookSettings> LoadWebhookAsync(string channel,CancellationToken token)=>Map(await Send<WorkerWebhookSettings>("GetWebhookSettings",channel,token));
    public async Task<NotificationWebhookSettings> SaveWebhookAsync(NotificationWebhookEdit edit,CancellationToken token)
    {
        var update=new WorkerWebhookUpdate(edit.ChannelId,edit.Enabled,edit.Revision,edit.Token,edit.SigningSecret,edit.ClearToken,edit.ClearSigningSecret);
        return Map(await Send<WorkerWebhookSettings>("SaveWebhookSettings",JsonSerializer.Serialize(update),token));
    }
    public async Task<NotificationSendResult> TestWebhookAsync(string channel,CancellationToken token)
    {var result=await Send<BackgroundNotificationResult>("TestWebhookSettings",channel,token);return new(result.State,result.Code);}
    private static NotificationWebhookSettings Map(WorkerWebhookSettings x)=>new(x.ChannelId,x.Enabled,x.HasToken,x.HasSigningSecret,x.Revision);
}
