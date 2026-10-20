using CRS.Application.Abstractions;
namespace CRS.Application;
public sealed partial class DesktopWorkflow
{
    public async Task<NotificationWebhookSettings> LoadWebhookSettingsAsync(string channel,CancellationToken token=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        return await (notifications??throw new NotSupportedException()).LoadWebhookAsync(channel,linked.Token);
    }
    public async Task<NotificationWebhookSettings> SaveWebhookSettingsAsync(NotificationWebhookEdit edit,CancellationToken token=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        return await (notifications??throw new NotSupportedException()).SaveWebhookAsync(edit,linked.Token);
    }
    public async Task<NotificationSendResult> TestWebhookSettingsAsync(string channel,CancellationToken token=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        return await (notifications??throw new NotSupportedException()).TestWebhookAsync(channel,linked.Token);
    }
}
