using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    /// <summary>沿既有唯一Quartz评价与投递；每轮最多三条，联网不持有市场库锁。</summary>
    private async Task<BackgroundJobResult> DispatchAlertNotificationsAsync(CancellationToken token)
    {
        if(context is not IBackgroundNotificationContext notificationContext)return BackgroundJobResult.Success("notifications=Unavailable");
        var count=0;
        for(var i=0;i<3;i++)
        {
            token.ThrowIfCancellationRequested();var item=await workspace!.ClaimAlertNotificationAsync(token);if(item is null)break;
            AlertDispatchOutcome outcome;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var result=await notificationContext.SendNotificationAsync(item.ChannelId,item.ProfileId,
                    new(item.MessageId,item.Subject,item.Body,"NationalTeamAlert",new Dictionary<string,string>{{"EventId",item.EventId},{"ChangeNo",item.ChangeNo.ToString(System.Globalization.CultureInfo.InvariantCulture)}}),deadline.Token).WaitAsync(deadline.Token);
                outcome=result.ChannelId==item.ChannelId&&result.MessageId==item.MessageId?new(result.State,result.Code,result.ProviderReceipt):new("OutcomeUnknown","AdapterResultMismatch");
            }
            // 领取后发生任何未分类中断都无法证明未受理；先持久核对状态，再响应宿主取消。
            catch(Exception){outcome=new("OutcomeUnknown","DeliveryInterrupted");}
            await workspace.CompleteAlertNotificationAsync(item,outcome);count++;
            token.ThrowIfCancellationRequested();
        }
        return BackgroundJobResult.Success("notificationAttempts="+count);
    }
    /// <summary>人工管理全部经过宿主授权和持久命令，原件只从白名单官方URL获取。</summary>
    private async Task<BackgroundJobResult> ManageAlertAsync(EtfRequest request,Guid? commandId,CancellationToken token)
    {
        try
        {
            switch(request.Operation)
            {
                case "alert-settings" when request.AlertConfiguration is {} settings:await workspace!.SaveAlertConfigurationAsync(settings,token,commandId);break;
                case "alert-retry":await workspace!.RetryAlertNotificationAsync(request.Revision??"",token,commandId);break;
                case "alert-document" when request.AlertDocument is {} document:
                    if(!Uri.TryCreate(document.Url,UriKind.Absolute,out var uri)||!OfficialEquityDocuments.IsAllowed(uri)||OfficialEquityDocuments.IsReviewedIssuer(uri))return new("Rejected","alert=OfficialEtfPdfRequired");
                    var response=await http!.GetAsync(uri,token);
                    if(response.StatusCode!=200)return new("RetryDeferred","alert=DocumentUnavailable",TimeSpan.FromMinutes(30));
                    await workspace!.RegisterAlertDocumentAsync(document,response.Content,token,commandId);break;
                case "alert-metadata-review" when request.AlertMetadata is {} metadata:await workspace!.ReviewAlertMetadataAsync(metadata,token,commandId);break;
                case "alert-holding-review" when request.NamedEtfHolding is {} holding:await workspace!.ReviewNamedEtfHoldingAsync(holding,token,commandId);break;
                default:return new("Rejected","alert=InvalidOperation");
            }
            await workspace!.EvaluateAlertsAsync(token);return BackgroundJobResult.Success();
        }
        catch(InvalidDataException){return new("Rejected","alert=InvalidOrStaleInput");}
    }
}
