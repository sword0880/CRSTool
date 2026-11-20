using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    private BackgroundRequest? industryRequest;
    public async Task<IndustryPeReport> IndustryPeReportAsync(DateOnly from,DateOnly through,CancellationToken token)
    {
        var page=await ReadPagesAsync<IndustryPeSeries>(new("industry-pe-report",From:from,To:through),token);
        var header=page.Metadata is {} metadata?EtfBusinessProtocol.ReadResponse<IndustryPeReport>(metadata):throw new InvalidDataException("行业报告缺少证据与日期元数据。");
        if(header.From!=from||header.Through!=through||page.Rows.Count!=11||page.Rows.Select(x=>x.Code).Distinct().Count()!=11
            ||page.Rows.Any(x=>!IndustryPeResearch.Sectors.TryGetValue(x.Code,out var name)||name!=x.Name))throw new InvalidDataException("行业报告身份或完整性不符，请更新后台服务。");
        return header with{Rows=page.Rows.AsReadOnly()};
    }
    public async Task<List<IndustryPeRevision>> IndustryPeRevisionsAsync(DateOnly from,DateOnly through,CancellationToken token)=>(await ReadPagesAsync<IndustryPeRevision>(new("industry-pe-revisions",From:from,To:through),token)).Rows;
    /// <summary>页面关闭或受理响应丢失时复用原请求；收到持久回执不等于源站或研究验收完成。</summary>
    public async Task<IndustryPeSyncReceipt> QueueIndustryPeAsync(DateOnly from,DateOnly through,bool refreshSource,CancellationToken token)
    {
        var payload=JsonSerializer.Serialize(new EtfRequest("industry-pe-sync",From:from,To:through,RefreshSource:refreshSource));
        if(industryRequest is {} prior&&prior.Payload!=payload)throw new InvalidDataException("已有未确认的行业请求，请先读取任务状态，不能换参数重复排队。");
        industryRequest??=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",Plugin,"industry-pe-sync",Payload:payload);
        var response=await SendAsync(industryRequest,token);
        if(response.Receipt is not {} receipt)throw new InvalidDataException("无法确认行业任务，原请求身份已保留，请使用相同参数核查。");
        return new(industryRequest.RequestId,receipt.State,receipt.Code,receipt.ExecutionId);
    }
    public async Task<IndustryPeSyncReceipt> IndustryPeSyncStatusAsync(Guid requestId,CancellationToken token)
    {
        var response=await SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:requestId),token);
        if(response.Code!="Ok"||response.Receipt is not {} receipt)throw new InvalidDataException("未取得行业任务的持久状态，保留原请求继续核查。");
        if(receipt.State=="Completed"&&industryRequest?.RequestId==requestId)industryRequest=null;
        return new(requestId,receipt.State,receipt.Code,receipt.ExecutionId);
    }
}
