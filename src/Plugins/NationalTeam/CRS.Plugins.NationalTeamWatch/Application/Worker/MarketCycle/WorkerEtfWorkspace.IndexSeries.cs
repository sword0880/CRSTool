using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    private readonly Dictionary<string,BackgroundRequest> indexRequests=new(StringComparer.Ordinal);
    private readonly object indexRequestGate=new();
    public async Task<IndexSeriesResearchReport> IndexSeriesReportAsync(DateOnly from,DateOnly through,CancellationToken token)
    {
        var page=await ReadPagesAsync<IndexSeriesResearchRow>(new("index-series-report",From:from,To:through),token);
        var header=page.Metadata is {} metadata?EtfBusinessProtocol.ReadResponse<IndexSeriesResearchReport>(metadata):throw new InvalidDataException("指数报告缺少范围及证据身份。");
        if(header.From!=from||header.Through!=through||page.Rows.Count!=IndexResearchCatalog.Definitions.Count||page.Rows.Select(x=>x.Code).Distinct().Count()!=page.Rows.Count||page.Rows.Any(x=>!IndexResearchCatalog.Definitions.TryGetValue(x.Code,out var definition)||x.Name!=definition.Name||x.Family!=definition.Family))throw new InvalidDataException("指数报告目录或日期不匹配，请更新后台服务。");
        return header with{Rows=page.Rows.AsReadOnly()};
    }
    public async Task<List<IndexSeriesRevision>> IndexSeriesRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token)=>(await ReadPagesAsync<IndexSeriesRevision>(new("index-series-revisions",From:from,To:through),token)).Rows;
    public Task<IndexSeriesCommandReceipt> QueueIndexSeriesAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token)=>QueueIndexAsync("index-series-sync",new("index-series-sync",From:from,To:through,RefreshSource:refreshSource),token);
    public Task<IndexSeriesCommandReceipt> QueueIndexValuationAsync(DateOnly date,CancellationToken token)=>QueueIndexAsync("index-valuation-sync",new("index-valuation-sync",To:date),token);
    private async Task<IndexSeriesCommandReceipt> QueueIndexAsync(string job,EtfRequest input,CancellationToken token)
    {
        var payload=JsonSerializer.Serialize(input);
        // 响应丢失只允许同参数沿原身份核查；改变日期不能绕过未知结果再创建请求。
        BackgroundRequest existing;lock(indexRequestGate)
        {
            if(indexRequests.TryGetValue(job,out var prior)&&prior.Payload!=payload)throw new InvalidDataException("已有未确认的指数请求，请保持原参数核查任务状态。");
            existing=prior??new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",Plugin,job,Payload:payload);indexRequests[job]=existing;
        }
        var reply=await SendAsync(existing,token);var receipt=reply.Receipt??throw new InvalidDataException("指数任务受理未知，原请求身份已保留，请用相同参数核查。");
        if(receipt.RequestId!=existing.RequestId)throw new InvalidDataException("指数回执请求身份不一致，保留原参数核查。");
        if(receipt.State=="Completed")lock(indexRequestGate){if(indexRequests.TryGetValue(job,out var current)&&current.RequestId==existing.RequestId)indexRequests.Remove(job);}return new(existing.RequestId,receipt.State,receipt.Code,receipt.ExecutionId);
    }
    public async Task<IndexSeriesCommandReceipt> IndexSeriesStatusAsync(Guid requestId,CancellationToken token)
    {
        var reply=await SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:requestId),token);
        if(reply.Code!="Ok"||reply.Receipt is not {} receipt||receipt.RequestId!=requestId)throw new InvalidDataException("指数任务状态或身份未知，保留原请求继续核查。");
        if(receipt.State=="Completed")lock(indexRequestGate){foreach(var key in indexRequests.Where(x=>x.Value.RequestId==requestId).Select(x=>x.Key).ToArray())indexRequests.Remove(key);}
        return new(requestId,receipt.State,receipt.Code,receipt.ExecutionId);
    }
}
