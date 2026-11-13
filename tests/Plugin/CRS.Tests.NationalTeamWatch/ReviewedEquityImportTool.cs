using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>只登记已逐页核对并编入候选的固定A股子集；原请求和业务回执分别保留。</summary>
internal static class ReviewedEquityImportTool
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private const string Reviewer="Codex逐页核对2026-11-04";
    private const string Reason="补齐已授权直接持股披露；只登记固定原文A股子集，性质及跨期可比性保持未知";
    public static async Task Run(string directory)
    {
        var root=Path.GetFullPath(directory);Directory.CreateDirectory(root);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(20));var token=deadline.Token;
        var pipe=new BackgroundPipeClient();var workspace=new WorkerEtfWorkspace(pipe);
        var before=await Send(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),token);
        if(before.Health is not{State:"Running"})throw new InvalidDataException("后台未健康运行，停止登记。");
        var documents=(await workspace.HistoricalEquityDocumentsAsync(token)).Where(x=>x.Scope=="ReviewedAShareDisclosedSubset")
            .OrderBy(x=>x.ReportAsOf).ThenBy(x=>x.Ticker,StringComparer.Ordinal).ToArray();
        if(documents.Length!=ReviewedEquitySources.All.Count||documents.Any(x=>ReviewedEquitySources.All.All(s=>s.Id!=x.Id||s.Sha256!=x.OriginalHash)))
            throw new InvalidDataException("后台未提供本轮完整固定原文目录，拒绝登记。");
        if(!File.Exists(Path.Combine(root,"before.json")))
            await Save(root,"before.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,Health=before.Health,Documents=documents,
                Watches=await workspace.EquityWatchlistAsync(token),Rows=await workspace.EquityHoldingsAsync(null,null,null,false,token)},token);
        var receipts=new List<HistoricalEquityReceipt>();
        foreach(var document in documents)
        {
            var item=Path.Combine(root,document.Id);Directory.CreateDirectory(item);
            var request=await Request(item,document,token);
            var receipt=await ImportOne(pipe,workspace,item,document,request,token);
            receipts.Add(receipt);
            await Save(root,"progress.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,Expected=documents.Length,Completed=receipts.Count,Receipts=receipts},token);
            Console.WriteLine($"IMPORTED {document.Ticker} {document.ReportAsOf:yyyy-MM-dd}：{receipt.Outcome}，已核A股账户{receipt.ReviewedAccounts}；不证明期间买卖。");
        }
        var after=await Send(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),token);
        var rows=await workspace.EquityHoldingsAsync(null,null,null,false,token);
        var healthStable=after.Health is{State:"Running"}&&after.Health.ProcessId==before.Health.ProcessId;
        await Save(root,"acceptance.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,Installed=true,Completed=receipts.Count,Expected=documents.Length,
            BeforeHealth=before.Health,AfterHealth=after.Health,HealthStable=healthStable,Receipts=receipts,Rows=rows,
            VerifiedStocks=EquityStockList.Build(rows,true),OtherDisclosures=EquityStockList.Build(rows,false),
            Boundary="原文披露A股子集；未列示持仓未知，不确认投资目的、交易或全市场覆盖"},token);
        if(!healthStable)throw new InvalidDataException("登记后后台健康或进程改变，保留回执并停止验收。");
    }
    private static async Task<BackgroundRequest> Request(string root,HistoricalEquityPdf document,CancellationToken token)
    {
        var path=Path.Combine(root,"request.json");
        var payload=EtfBusinessProtocol.WriteRequest(new("equity-history-import",Revision:document.Id,Reviewer:Reviewer,Reason:Reason,Accept:true,Original:true));
        if(File.Exists(path))
        {
            var saved=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(path,token))??throw new InvalidDataException("原登记请求损坏。");
            if(saved.Command!="RunNow"||saved.PluginId!="crs.national-team"||saved.JobId!="equity-history-import"||saved.Payload!=payload)
                throw new InvalidDataException("原登记请求不能更换证券、定义或核验参数。");
            return saved;
        }
        var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","equity-history-import",Payload:payload);
        await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await JsonSerializer.SerializeAsync(file,request,Json,token);await file.FlushAsync(token);return request;
    }
    private static async Task<HistoricalEquityReceipt> ImportOne(BackgroundPipeClient pipe,WorkerEtfWorkspace workspace,string root,HistoricalEquityPdf document,BackgroundRequest request,CancellationToken token)
    {
        // 已受理的原请求只回查，不重派；短暂准入延期沿宿主原期限自动继续。
        if(!File.Exists(Path.Combine(root,"submitted.json")))
        {var accepted=await Send(pipe,request,token);await Save(root,"submitted.json",accepted,token);}
        BackgroundResponse command;
        var deferredObserved=false;
        while(true)
        {
            command=await Send(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),token);
            if(command.Receipt is null)throw new InvalidDataException("原命令缺少持久回执，不创建替代请求。");
            if(command.Receipt.State=="Completed")break;
            if(command.Receipt.Code=="RetryDeferred"&&!deferredObserved)
            {
                await Save(root,"deferred-command.json",command,token);
                var health=await Send(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),token);
                var activity=await Send(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity",PageSize:50),token);
                await Save(root,"deferred-health.json",health,token);await Save(root,"deferred-activity.json",activity,token);
                if(health.Health is not{State:"Running"}||health.Health.Sources?.Any(x=>x.Source=="CNINFO"&&x.State=="Cooldown")==true)
                    throw new InvalidDataException("后台不可用或固定来源正在冷却，保留原ID和期限停止后续登记。");
                Console.WriteLine($"WAIT 原请求{request.RequestId}暂时延期；只回查，不重派、不修改宿主重试时间。");
                deferredObserved=true;
            }
            await Task.Delay(1000,token);
        }
        await Save(root,"command.json",command,token);
        if(command.Receipt?.ExecutionId is {} executionId)
        {
            var execution=await Send(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:executionId),token);
            await Save(root,"execution.json",execution,token);
            if(execution.Execution?.Result!="Succeeded")throw new InvalidDataException("原执行未成功，保留原ID及延期信息。");
        }
        if(command.Receipt is not{State:"Completed",Code:"Succeeded"})throw new InvalidDataException("原登记尚未完成；保留原ID停止后续登记。");
        var receipt=await workspace.HistoricalEquityReceiptAsync(request.RequestId,token);
        await Save(root,"business-receipt.json",receipt,token);
        if(receipt is null||receipt.RequestId!=request.RequestId||receipt.DefinitionId!=document.Id||receipt.OriginalHash!=document.OriginalHash
            ||receipt.ExtractHash!=document.ExtractHash||receipt.ReviewedAccounts!=document.ReviewedAccounts)
            throw new InvalidDataException("宿主与业务回执、原文或A股子集身份不一致。");
        return receipt;
    }
    private static async Task<BackgroundResponse> Send(BackgroundPipeClient pipe,BackgroundRequest request,CancellationToken token)
    {
        while(true)
        {
            var response=await pipe.SendAsync(request,token);
            if(response.Code is "RateLimited" or "PluginBusy"){await Task.Delay(500,token);continue;}
            if(response.Code is not ("Ok" or "Accepted" or "Pending"))throw new InvalidDataException("后台请求未完成："+response.Code);
            return response;
        }
    }
    private static async Task Save<T>(string root,string name,T value,CancellationToken token)
    {
        var path=Path.Combine(root,name);var temporary=path+".tmp";
        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(value,Json),token);token.ThrowIfCancellationRequested();File.Move(temporary,path,true);
    }
}
