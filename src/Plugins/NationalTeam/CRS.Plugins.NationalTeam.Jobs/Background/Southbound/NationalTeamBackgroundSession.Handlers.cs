using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    [NationalTeamQuery("southbound-net-annual")]
    private async Task<NationalTeamQueryResult> QuerySouthboundAnnual(EtfRequest request,CancellationToken token)
    {
        if(request.From is not {} from||request.To is not {} through||from!=new DateOnly(from.Year,1,1)||through!=new DateOnly(through.Year,12,31))throw new InvalidDataException("南向年度查询参数无效。");
        var report=await workspace!.Southbound.AnnualAsync(from.Year,through.Year,token);
        var serialization=new NationalTeamQuerySerialization(token);
        return new(CRS.Serialization.JsonDocumentBlocks.Encode(report,token).Select(x=>serialization.Row(x)).ToArray());
    }
    [NationalTeamJob("southbound-sync","南向净买入历史更新","0 40 23 * * ?","China Standard Time",true,false,Group="collection",MaximumConcurrency=2,Order=29)]
    private async Task<BackgroundJobResult> ExecuteSouthboundSync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(request.Operation is not ("sync" or "southbound-net-sync"))throw new InvalidDataException("南向净买入更新任务参数无效。");
        if(commandId is {} id&&await workspace!.Southbound.ReceiptAsync(id,token) is {} prior)return BackgroundJobResult.Success("southbound="+prior);
        var through=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);var from=SouthboundAnnualComparison.SourceFrom;
        var pages=new List<SouthboundCapturedPage>();
        for(var page=1;page<=5;page++)
        {
            var uri=SouthboundHistorySource.PageUri(from,through,page);
            try
            {
                var response=await http!.GetAsync(uri,token);
                if(response.StatusCode!=200)return new("Failed","southbound=SourceHttp;status="+response.StatusCode,TimeSpan.FromMinutes(30));
                var parsed=SouthboundHistorySource.ParsePage(response.Content,from,through);
                pages.Add(new(uri.AbsoluteUri,response.Content,DateTimeOffset.UtcNow));
                if(page==parsed.Pages)break;
            }
            catch(SourceBudgetUnavailableException error){return new("RetryDeferred","southbound="+error.Code,error.Until is {} until?until-CRS.Timing.BeijingClock.Now:TimeSpan.FromMinutes(5));}
            // 旧宿主没有新增来源预算时只拒绝本功能，不影响已有ETF或股票采集的启动。
            catch(ArgumentException){return new("Rejected","southbound=HostSourceBudgetUnsupported");}
            catch(Exception error) when(error is TimeoutException or System.Net.Http.HttpRequestException){return new("Failed","southbound=SourceUnavailable",TimeSpan.FromMinutes(30));}
            catch(InvalidDataException){(http as IOfficialSourceValidationFeedback)?.RejectContract(uri);return new("NeedsReview","southbound=SourceContractChanged");}
        }
        try
        {
            var capture=new SouthboundCapture(from,through,pages);_=SouthboundHistorySource.ParseCapture(capture);
            var saved=await workspace!.Southbound.SaveAsync(capture,commandId,token);return BackgroundJobResult.Success("southbound="+saved);
        }
        catch(InvalidDataException){return new("NeedsReview","southbound=IncompleteOrChangedSnapshot");}
    }
}
