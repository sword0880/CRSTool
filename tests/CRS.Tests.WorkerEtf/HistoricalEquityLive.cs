using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Worker.Ipc;

/// <summary>显式实机入口：先保存原请求，再只观察同一身份；受理、延期和观察超时都不是金融登记成功。</summary>
internal static class HistoricalEquityLive
{
    private const string Definition="cnd-600153-20251231-pdf-v1";
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static async Task Run(string output,bool submit)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);var path=Path.Combine(output,"request.json");
        using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=limit.Token;var pipe=new BackgroundPipeClient();var reader=new WorkerEtfWorkspace(pipe);
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {while(true){var reply=await pipe.SendAsync(request,token);if(reply.Code!="RateLimited")return reply;await Task.Delay(500,token);}}
        async Task Save(string file,object value)=>await File.WriteAllTextAsync(Path.Combine(output,file),JsonSerializer.Serialize(value,Json),token);
        BackgroundRequest request;
        if(File.Exists(path))request=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(path,token))??throw new InvalidDataException("原请求文件无效，不能创建替代请求。");
        else
        {
            if(!submit)throw new InvalidDataException("未找到已保存的原请求。");
            request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","equity-history-import",Payload:JsonSerializer.Serialize(new EtfRequest("equity-history-import",Revision:Definition,
                Reviewer:"公开原件核验（研究助手）",Reason:"已逐页核对P158—159及P2；完整十户/四具名账户；公告列表日非最早公开时刻；性质和可比性保持未知",Original:true,Accept:true)));
            await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            await JsonSerializer.SerializeAsync(file,request,Json,token);await file.FlushAsync(token);file.Flush(true);
        }
        var payload=EtfBusinessProtocol.ParseRequest(request.Payload??"");
        if(request.Command!="RunNow"||request.PluginId!="crs.national-team"||request.JobId!="equity-history-import"||payload.Operation!="equity-history-import"||payload.Revision!=Definition||!payload.Original||!payload.Accept||payload.Comparable)
            throw new InvalidDataException("已保存请求的身份或口径不符，拒绝重放。");
        var state=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));
        if(submit&&state.Code=="UnknownRequest")
        {
            var pending=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory","crs.national-team","equity-history-import",PageSize:1));
            await Save("pending-before.json",pending);
            if(pending.Code!="Ok"||pending.PendingRequests is null||pending.PendingRequests.Any(x=>x.PluginId=="crs.national-team"&&x.JobId=="equity-history-import"))
                throw new InvalidDataException("不能确认没有既有历史请求，保留当前身份，拒绝另起任务。");
            await Save("health-before.json",await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth")));
            await Save("disclosures-before.json",await reader.EquityDisclosuresAsync(new("SSE","600153"),token));
            await Save("events-before.json",await reader.EquityEventsAsync(new("SSE","600153"),token));
            state=await Send(request);await Save("submitted.json",state);
        }
        // 文件已经持久化。响应不明或观察超时可再次使用status入口，不以新ID重新排队。
        while(true)
        {
            state=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));await Save("command.json",state);
            if(state.Code!="Ok"||state.Receipt is null)throw new InvalidDataException("原请求状态未知，保存身份后停止；不得重复启动。");
            if(!submit||state.Receipt.State=="Completed"||state.Receipt.Code=="RetryDeferred")break;
            await Task.Delay(1000,token);
        }
        var execution=state.Receipt?.ExecutionId is {} id?await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:id)):null;
        var receipt=await reader.HistoricalEquityReceiptAsync(request.RequestId,token);
        await Save("status.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,Command=state,Execution=execution,Business=receipt});
        if(state.Receipt is not{State:"Completed",Code:"Succeeded"}||receipt is null)
        {Console.WriteLine($"UNCONFIRMED {request.RequestId} {state.Receipt?.State}/{state.Receipt?.Code}；保留原请求及来源冷却。");Environment.ExitCode=2;return;}
        var definition=(await reader.HistoricalEquityDocumentsAsync(token)).Single(x=>x.Id==Definition);
        if(receipt.DefinitionId!=Definition||receipt.OriginalHash!=definition.OriginalHash||receipt.ExtractHash!=definition.ExtractHash||receipt.ReviewedAccounts!=4)throw new InvalidDataException("业务回执与固定原文不符。");
        var disclosures=await reader.EquityDisclosuresAsync(new("SSE","600153"),token);var events=await reader.EquityEventsAsync(new("SSE","600153"),token);
        await Save("disclosures-after.json",disclosures);await Save("events-after.json",events);await Save("health-after.json",await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth")));
        var disclosure=disclosures.Single(x=>x.Id==receipt.DisclosureId);var historical=events.Where(x=>x.DisclosureId==receipt.DisclosureId).ToArray();
        if(disclosure.State!="Accepted"||disclosure.Holders.Count!=10||historical.Length!=4||historical.Any(x=>x.Nature!="Unknown"||x.Delta is not null))throw new InvalidDataException("真实回读的完整表、未知性质或不可比边界不符。");
        Console.WriteLine($"VERIFIED {request.RequestId} {receipt.Outcome}；完整十户/四具名账户，性质及可比性未知，不确认交易。");
    }
}
