using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    private async Task<BackgroundJobResult> SearchEtfReportsAsync(EtfRequest request,Guid? commandId,CancellationToken token)
    {
        var key=Key(request);var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
        var from=request.From??today.AddDays(-800);var through=request.To??today;
        try{_=OfficialEtfReportIndex.Request(key,from,through,1);}
        catch(InvalidDataException){return new("Rejected","etf-reports=InvalidRange");}
        if(through>today)return new("Rejected","etf-reports=FutureDate");
        if(commandId is {} committed&&await workspace!.Holdings.EtfReportCommandCompleteAsync(committed,key,from,through,token))return BackgroundJobResult.Success("etf-reports=AlreadyCommitted");
        if(!(await workspace!.Etf.ConfigurationAsync(token)).Any(x=>x.Market==key.Market&&x.Ticker==key.Ticker))return new("Rejected","etf-reports=NotConfigured");
        if(key.Market=="SZSE"&&http is not IPluginJsonHttpGateway)return new("Rejected","etf-reports=JsonQueryCapabilityMissing");
        var pages=new List<byte[]>();var count=1;var currentUri=OfficialEtfReportIndex.Request(key,from,through,1);
        try
        {
            for(var page=1;page<=count;page++)
            {
                currentUri=OfficialEtfReportIndex.Request(key,from,through,page);
                var body=OfficialEtfReportIndex.RequestBody(key,from,through,page);
                var response=body is null?await http!.GetAsync(currentUri,token):await ((IPluginJsonHttpGateway)http!).PostJsonAsync(currentUri,body,token);
                if(response.StatusCode!=200)return new("RetryDeferred","etf-reports=SourceUnavailable;http="+response.StatusCode,TimeSpan.FromMinutes(30));
                var parsed=OfficialEtfReportIndex.Parse(key,from,through,page,response.Content,token);count=parsed.Pages;pages.Add(response.Content);
                if(pages.Sum(x=>(long)x.Length)>OfficialEtfReportIndex.MaximumCaptureBytes)throw new InvalidDataException("ETF目录超过整次查询限额。");
                await ReportAsync(new("ReportIndexPage",Market:key.Market,Received:parsed.Documents.Count));
            }
            _=OfficialEtfReportIndex.ParseCapture(key,from,through,pages,token);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(InvalidDataException)
        {
            (http as IOfficialSourceValidationFeedback)?.RejectContract(currentUri);
            return new("RetryDeferred","etf-reports=ContractOrPaginationIncomplete",TimeSpan.FromMinutes(30));
        }
        catch(SourceBudgetUnavailableException error)
        {return new("RetryDeferred","etf-reports="+error.Code,error.Until is {} until&&until>CRS.Timing.BeijingClock.Now?until-CRS.Timing.BeijingClock.Now:TimeSpan.FromMinutes(30));}
        catch(Exception error) when(error is HttpRequestException or TimeoutException or InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
        {return new("RetryDeferred","etf-reports=SourceUnavailable",TimeSpan.FromMinutes(30));}
        // 来源协议核验在网络阶段结束；存储失败由宿主处理，不误记成来源冷却。
        await workspace.Holdings.SaveReportIndexAsync(key,from,through,pages,token,commandId);
        await ReportAsync(new("ReportIndexCommitted",Market:key.Market,Added:pages.Count));
        return BackgroundJobResult.Success($"etf-reports=IndexedUnreviewed;pages={pages.Count};holdingsConfirmed=false");
    }
}
