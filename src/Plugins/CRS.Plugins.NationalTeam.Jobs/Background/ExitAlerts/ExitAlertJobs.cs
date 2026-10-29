using CRS.Worker.Abstractions;
using CRS.Plugin.Abstractions;
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
            token.ThrowIfCancellationRequested();var item=await workspace!.Alerts.ClaimAlertNotificationAsync(token);if(item is null)break;
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
            await workspace.Alerts.CompleteAlertNotificationAsync(item,outcome);count++;
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
                case "alert-settings" when request.AlertConfiguration is {} settings:await workspace!.Alerts.SaveAlertConfigurationAsync(settings,token,commandId);break;
                case "alert-retry":await workspace!.Alerts.RetryAlertNotificationAsync(request.Revision??"",token,commandId);break;
                case "alert-document" when request.AlertDocument is {} document:
                    if(!Uri.TryCreate(document.Url,UriKind.Absolute,out var uri)||!OfficialEquityDocuments.IsAllowed(uri)||OfficialEquityDocuments.IsReviewedIssuer(uri))return new("Rejected","alert=OfficialEtfPdfRequired");
                    PublicHttpResponse response;
                    // 只在下载边界分类来源异常；原件/存储故障不能伪装成网络退避，调用方取消仍向宿主传播。
                    try{response=await http!.GetAsync(uri,token);}
                    catch(SourceBudgetUnavailableException error)
                    {
                        token.ThrowIfCancellationRequested();
                        var reason=error.Code switch{"SourceCooldown"=>"DocumentSourceCooldown","SourceQueueFull"=>"DocumentSourceQueueFull",_=>"DocumentSourceUnavailable"};
                        return AlertDocumentDeferred(uri,reason,error.Until);
                    }
                    catch(HttpRequestException error)
                    {
                        token.ThrowIfCancellationRequested();
                        return AlertDocumentDeferred(uri,"DocumentConnectionFailed",error.Data["SourceRetryAt"] as DateTimeOffset?);
                    }
                    catch(TimeoutException error)
                    {
                        token.ThrowIfCancellationRequested();
                        return AlertDocumentDeferred(uri,"DocumentTimedOut",error.Data["SourceRetryAt"] as DateTimeOffset?);
                    }
                    token.ThrowIfCancellationRequested();
                    if(response.StatusCode!=200)return AlertDocumentDeferred(uri,"DocumentHttpUnavailable",httpStatus:response.StatusCode);
                    await workspace!.Alerts.RegisterAlertDocumentAsync(document,response.Content,token,commandId);break;
                case "alert-metadata-review" when request.AlertMetadata is {} metadata:await workspace!.Alerts.ReviewAlertMetadataAsync(metadata,token,commandId);break;
                case "alert-holding-review" when request.NamedEtfHolding is {PublicationPrecision:EtfPublicationPrecision.ExactTime,PublicationDate:null,PublishedAt:not null} holding:
                    await workspace!.Alerts.ReviewNamedEtfHoldingAsync(holding,token,commandId);break;
                case "alert-holding-review-date-v1" when request.NamedEtfHolding is {PublicationPrecision:EtfPublicationPrecision.DateOnly,PublicationDate:not null,PublishedAt:null} datedHolding:
                    await workspace!.Alerts.ReviewNamedEtfHoldingAsync(datedHolding,token,commandId);break;
                default:return new("Rejected","alert=InvalidOperation");
            }
            await workspace!.Alerts.EvaluateAlertsAsync(token);return BackgroundJobResult.Success();
        }
        catch(InvalidDataException){return new("Rejected","alert=InvalidOrStaleInput");}
    }
    private BackgroundJobResult AlertDocumentDeferred(Uri uri,string reason,DateTimeOffset? until=null,int? httpStatus=null)
    {
        var now=CRS.Timing.BeijingClock.Now;
        var retry=now.Add(reason=="DocumentSourceQueueFull"?TimeSpan.FromSeconds(30):TimeSpan.FromMinutes(30));
        var local=(http as OfficialHttpGateway)?.RetryAt(uri);
        if(local>retry)retry=local.Value;
        if(until>retry)retry=until.Value;
        var status=httpStatus is{} value?";http="+value.ToString(System.Globalization.CultureInfo.InvariantCulture):"";
        return new("RetryDeferred","alert="+reason+status+";retryAt="+retry.ToOffset(TimeSpan.FromHours(8)).ToString("O"),retry-now);
    }
}
