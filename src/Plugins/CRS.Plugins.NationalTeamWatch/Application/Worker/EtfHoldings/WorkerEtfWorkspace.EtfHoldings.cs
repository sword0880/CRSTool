using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Serialization;
using CRS.Worker.Ipc;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    public async Task<NamedEtfHoldingHistoryReport> EtfHoldingHistoryAsync(EtfKey key,DateOnly asOf,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("etf-holding-history",key.Market,key.Ticker,To:asOf,Count:2),token)).Rows;
        var report=JsonDocumentBlocks.Decode<NamedEtfHoldingHistoryReport>(blocks,token);
        if(report.FormatVersion!=NamedEtfHoldingHistoryReport.Version||report.Input.Key!=key||report.Input.AsOf!=asOf)throw new InvalidDataException("具名ETF历史证券、统计日或版本不符。");
        var rebuilt=NamedEtfHoldingHistory.Evaluate(report.Input);
        if(rebuilt.Fingerprint!=report.Fingerprint)throw new InvalidDataException("具名ETF历史归一输入或结果指纹不符。");return rebuilt;
    }
    private readonly Dictionary<(EtfKey Key,DateOnly From,DateOnly Through),BackgroundRequest> etfReportSearches=[];
    public async Task<EtfReportIndexSnapshot> EtfReportIndexAsync(EtfKey key,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("etf-report-index",key.Market,key.Ticker,Count:2),token)).Rows;
        var result=JsonDocumentBlocks.Decode<EtfReportIndexSnapshot>(blocks,token);
        if(result.Key!=key||result.FormatVersion!=EtfReportIndexSnapshot.Version||result.State is not ("NotSearched" or "IndexedUnreviewed" or "EvidenceUnavailable")
            ||result.State=="NotSearched"&&(result.SearchId is not null||result.DeclaredTotal is not null||result.Documents.Count!=0)
            ||result.State!="NotSearched"&&(result.SearchId is null||result.From is null||result.Through is null||result.CapturedAt is null||result.DeclaredTotal!=result.Documents.Count)
            ||result.Documents.Any(x=>!x.SecurityCodes.Contains(key.Ticker)))throw new InvalidDataException("ETF报告目录证券、版本或完整性不符。");
        return result;
    }
    public async Task<EtfReportSearchReceipt> QueueEtfReportsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token)
    {
        var scope=(key,from,through);
        if(!etfReportSearches.TryGetValue(scope,out var request))
        {
            if(etfReportSearches.Count>=128)throw new InvalidDataException("尚有较多ETF检索请求未核对，请先查询已有请求的结果。");
            request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",Plugin,"etf-report-search",Payload:JsonSerializer.Serialize(new EtfRequest("etf-report-search",key.Market,key.Ticker,from,through)));
            etfReportSearches.Add(scope,request);
        }
        var response=await SendAsync(request,token);
        if(response.Code is not ("Accepted" or "Succeeded" or "RetryDeferred")&&response.Receipt is null)
        {etfReportSearches.Remove(scope);throw new InvalidDataException("后台未接受ETF报告检索："+response.Code);}
        if(response.Receipt?.State=="Completed")etfReportSearches.Remove(scope);
        return new(request.RequestId,response.Receipt?.State??response.Code,response.Receipt?.Code??response.Code);
    }
}
