using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    private readonly SemaphoreSlim historicalCommandGate=new(1,1);
    private BackgroundRequest? historicalRequest;
    private Guid? historicalObservedRequest;
    public async Task<List<HistoricalEquityPdf>> HistoricalEquityDocumentsAsync(CancellationToken token)
        =>(await ReadPagesAsync<HistoricalEquityPdf>(new("equity-history-documents"),token)).Rows;
    public async Task<HistoricalEquityReceipt?> HistoricalEquityReceiptAsync(Guid requestId,CancellationToken token)
    {
        var rows=(await ReadPagesAsync<HistoricalEquityReceipt>(new("equity-history-receipt",Revision:requestId.ToString("N")),token)).Rows;
        if(rows.Count>1||rows.Any(x=>x.RequestId!=requestId||string.IsNullOrWhiteSpace(x.DefinitionId)||string.IsNullOrWhiteSpace(x.DisclosureId)
            ||x.Outcome is not ("Imported" or "AlreadyRegistered")||x.ReviewedAccounts is <1 or >10
            ||x.OriginalHash.Length!=64||!x.OriginalHash.All(Uri.IsHexDigit)||x.ExtractHash.Length!=64||!x.ExtractHash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("历史业务回执身份、口径或数量不符。");
        return rows.SingleOrDefault();
    }
    public async Task<HistoricalEquityCommand> ImportHistoricalEquityPdfAsync(string id,string reviewer,string reason,bool originalConfirmed,CancellationToken token)
    {
        if(!originalConfirmed||string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(reviewer)||reviewer.Trim().Length>100||string.IsNullOrWhiteSpace(reason)||reason.Trim().Length>500)
            throw new InvalidDataException("须核对固定原文，并填写审核人和登记理由。");
        var payload=JsonSerializer.Serialize(new EtfRequest("equity-history-import",Revision:id,Reviewer:reviewer.Trim(),Reason:reason.Trim(),Accept:true,Original:true));
        _=EtfBusinessProtocol.ParseRequest(payload);await historicalCommandGate.WaitAsync(token);
        try
        {
            if(historicalRequest is null&&historicalObservedRequest is {} observedId)
                return await ConfirmHistoricalState((await ReadHistoricalStatus(observedId,token)) with{ExistingRequest=true},token);
            if(historicalRequest is {} pending&&pending.Payload!=payload)throw new InvalidDataException("已有未确认历史登记，请保留原参数核查原请求。");
            if(historicalRequest is {} originalRequest)
            {
                // 响应不明或等待重试时先查原句柄；只有服务明确UnknownRequest才可按原ID原参数重放提交。
                var observed=await SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:originalRequest.RequestId),token);
                if(observed.Code=="Ok"&&observed.Receipt is {} found&&found.RequestId==originalRequest.RequestId)
                {return await ConfirmHistoricalState(new(found.RequestId,found.State,found.Code,found.ExecutionId),token);}
                if(observed.Code!="UnknownRequest")throw new InvalidDataException("原历史请求状态尚未确认，保留身份，不重复启动。");
            }
            if(historicalRequest is null)
            {
                // 重开桌面先回查服务端待处理请求；参数不可见时只返回原身份，绝不用新参数重放旧ID。
                var history=await SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory",Plugin,"equity-history-import",PageSize:1),token);
                if(history.Code!="Ok"||history.PendingRequests is null)throw new InvalidDataException("无法确认历史登记待处理队列，拒绝重复提交。");
                if(history.PendingRequests.FirstOrDefault(x=>x.PluginId==Plugin&&x.JobId=="equity-history-import") is {} original)
                {
                    historicalObservedRequest=original.RequestId;
                    var state=await ReadHistoricalStatus(original.RequestId,token);
                    return await ConfirmHistoricalState(state with{ExistingRequest=true},token);
                }
                historicalRequest=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",Plugin,"equity-history-import",Payload:payload);
            }
            var request=historicalRequest;var reply=await SendAsync(request,token);
            if(reply.Receipt is not {} receipt||receipt.RequestId!=request.RequestId)throw new InvalidDataException("历史登记受理未知，已保留原ID与参数，请核查原请求。");
            return await ConfirmHistoricalState(new(receipt.RequestId,receipt.State,receipt.Code,receipt.ExecutionId),token);
        }
        finally{historicalCommandGate.Release();}
    }
    private async Task<HistoricalEquityCommand> ReadHistoricalStatus(Guid id,CancellationToken token)
    {
        var reply=await SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:id),token);
        if(reply.Code!="Ok"||reply.Receipt is not {} receipt||receipt.RequestId!=id)throw new InvalidDataException("原历史请求状态未知，不创建替代请求。");
        return new(id,receipt.State,receipt.Code,receipt.ExecutionId);
    }
    private async Task<HistoricalEquityCommand> ConfirmHistoricalState(HistoricalEquityCommand state,CancellationToken token)
    {
        if(state.State!="Completed")return state;
        if(state.Code=="Succeeded")
        {
            var receipt=await HistoricalEquityReceiptAsync(state.RequestId,token);
            if(receipt is null)return state with{State="Unconfirmed",Code="BusinessReceiptMissing"};
            if(historicalRequest?.RequestId==state.RequestId&&EtfBusinessProtocol.ParseRequest(historicalRequest.Payload!).Revision!=receipt.DefinitionId)
                throw new InvalidDataException("宿主和历史业务回执对应的原文不同，保留原请求核查。");
        }
        // 只有宿主终态及成功时的业务回执均确认后才解除原参数约束。
        if(historicalRequest?.RequestId==state.RequestId)historicalRequest=null;
        if(historicalObservedRequest==state.RequestId)historicalObservedRequest=null;
        return state;
    }
    public async Task<HistoricalEquityCommand> HistoricalEquityStatusAsync(Guid requestId,CancellationToken token)
    {
        await historicalCommandGate.WaitAsync(token);
        try{return await ConfirmHistoricalState(await ReadHistoricalStatus(requestId,token),token);}
        finally{historicalCommandGate.Release();}
    }
}
