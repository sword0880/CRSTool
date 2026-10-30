using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    [NationalTeamJob(EtfDocumentCollectionProtocol.SseJob,"沪市ETF官方原件采集（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=25)]
    private Task<BackgroundJobResult> ExecuteSseEtfDocument(EtfRequest request,CancellationToken token,Guid? commandId)
        =>CollectEtfDocumentAsync(request,"SSE",commandId,token);

    [NationalTeamJob(EtfDocumentCollectionProtocol.SzseJob,"深市ETF官方原件采集（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=26)]
    private Task<BackgroundJobResult> ExecuteSzseEtfDocument(EtfRequest request,CancellationToken token,Guid? commandId)
        =>CollectEtfDocumentAsync(request,"SZSE",commandId,token);

    private async Task<BackgroundJobResult> CollectEtfDocumentAsync(EtfRequest request,string market,Guid? commandId,CancellationToken token)
    {
        if(commandId is null||commandId==Guid.Empty||request.Operation!=EtfDocumentCollectionProtocol.Operation||request.AlertDocument is not {} document||document.Market!=market
            ||document.Ticker is not{Length:6} ticker||!ticker.All(char.IsAsciiDigit)
            ||!Uri.TryCreate(document.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||!EtfDocumentCollectionProtocol.MatchesSource(market,uri))
            return new("Rejected","document=InvalidOperationOrMarket");
        try
        {
            if(await workspace!.Holdings.DocumentCommandCompleteAsync(commandId.Value,document,token))
                return BackgroundJobResult.Success("document=Registered;reusedCommitted=True;evaluation=IndependentJob");
            if(!(await workspace!.Etf.ConfigurationAsync(token)).Any(x=>x.Market==market&&x.Ticker==ticker))return new("Rejected","document=EtfNotConfigured");
            var result=await DownloadAndRegisterEtfDocumentAsync(document,commandId,token,true);
            // 登记与待评价标记在同一事务；采集不等待长评价，也不自动审核真实持仓。
            return result.Status=="Succeeded"?result with{Checkpoint="document=Registered;evaluation=IndependentJob"}:result;
        }
        catch(InvalidDataException){return new("Rejected","alert=InvalidOrStaleInput");}
    }

    /// <summary>新版独立采集及旧持久命令共用下载边界；旧入口仍由原调用者执行其同步评价。</summary>
    private async Task<BackgroundJobResult> DownloadAndRegisterEtfDocumentAsync(AlertDocumentRequest document,Guid? commandId,CancellationToken token,bool isolated=false)
    {
        if(!Uri.TryCreate(document.Url,UriKind.Absolute,out var uri)||!OfficialEquityDocuments.IsAllowed(uri)||OfficialEquityDocuments.IsReviewedIssuer(uri))return new("Rejected","alert=OfficialEtfPdfRequired");
        PublicHttpResponse response;
        // 只在下载边界分类来源异常，存储故障与调用方取消不能伪装成来源等待。
        try{response=await http!.GetAsync(uri,token);}
        catch(SourceBudgetUnavailableException error)
        {
            token.ThrowIfCancellationRequested();
            var reason=error.Code switch{"SourceCooldown"=>"DocumentSourceCooldown","SourceQueueFull"=>"DocumentSourceQueueFull",_=>"DocumentSourceUnavailable"};
            return AlertDocumentDeferred(uri,reason,error.Until);
        }
        catch(HttpRequestException error)
        {token.ThrowIfCancellationRequested();return AlertDocumentDeferred(uri,"DocumentConnectionFailed",error.Data["SourceRetryAt"] as DateTimeOffset?);}
        catch(TimeoutException error)
        {token.ThrowIfCancellationRequested();return AlertDocumentDeferred(uri,"DocumentTimedOut",error.Data["SourceRetryAt"] as DateTimeOffset?);}
        token.ThrowIfCancellationRequested();
        if(response.StatusCode!=200)return AlertDocumentDeferred(uri,"DocumentHttpUnavailable",httpStatus:response.StatusCode);
        if(isolated)await workspace!.Holdings.RegisterDocumentAsync(document,response.Content,commandId!.Value,token);
        else await workspace!.Holdings.RegisterAlertDocumentAsync(document,response.Content,token,commandId);
        return BackgroundJobResult.Success();
    }

    private BackgroundJobResult AlertDocumentDeferred(Uri uri,string reason,DateTimeOffset? until=null,int? httpStatus=null)
    {
        var now=CRS.Timing.BeijingClock.Now;var local=(http as OfficialHttpGateway)?.RetryAt(uri);
        var knownCooldown=reason=="DocumentSourceCooldown"&&(until>now||local>now);
        var retry=now.Add(reason=="DocumentSourceQueueFull"||knownCooldown?TimeSpan.FromSeconds(30):TimeSpan.FromMinutes(30));
        if(local>retry)retry=local.Value;if(until>retry)retry=until.Value;
        var status=httpStatus is{} value?";http="+value.ToString(System.Globalization.CultureInfo.InvariantCulture):"";
        return new("RetryDeferred","alert="+reason+status+";retryAt="+retry.ToOffset(TimeSpan.FromHours(8)).ToString("O"),retry-now);
    }
}
