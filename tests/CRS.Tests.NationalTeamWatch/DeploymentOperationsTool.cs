using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>本机发布留证：只读状态；报告目录命令显式执行、先保存稳定请求ID，不审核持仓或发送通知。</summary>
internal static class DeploymentOperationsTool
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static async Task State(string directory)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=deadline.Token;
        var pipe=new BackgroundPipeClient();var reader=new WorkerEtfWorkspace(pipe);
        var before=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),token);
        var capabilities=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",
            Payload:EtfBusinessProtocol.WriteRequest(new("query-capabilities"))),token);
        if(capabilities.Code is not ("Ok" or "UnknownQuery"))throw new InvalidDataException("查询能力协商未取得明确响应："+capabilities.Code);
        var jobs=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetJobs"),token);
        var history=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory","crs.national-team"),token);
        var configuration=await MonitorReadinessTool.Read(()=>reader.ConfigurationAsync(token),token);
        var holdings=await MonitorReadinessTool.Read(()=>reader.EquityHoldingsAsync(null,null,null,false,token),token);
        var after=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),token);
        if(before.Health is not {State:"Running"}||after.Health is not {State:"Running"}||before.Health.ProcessId!=after.Health.ProcessId||jobs.Jobs is null||history.History is null)
            throw new InvalidDataException("服务状态、进程或正式记录不完整，不生成完整发布状态。");
        await Save(directory,"state.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,Health=after.Health,QueryCapabilities=capabilities,Jobs=jobs.Jobs,History=history.History,Configuration=configuration,Equity=holdings},token);
        Console.WriteLine($"READ 正式服务Running，PID={after.Health.ProcessId}，计划{jobs.Jobs.Count}项，ETF配置{configuration.Count}只，股票记录{holdings.Count}条；完整状态已保存。");
    }
    public static async Task Compare(string beforeDirectory,string afterDirectory)
    {
        using var before=JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(beforeDirectory,"state.json")));
        using var after=JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(afterDirectory,"state.json")));
        var oldConfiguration=JsonSerializer.Deserialize<EtfConfiguration[]>(before.RootElement.GetProperty("Configuration"))!;
        var newConfiguration=JsonSerializer.Deserialize<EtfConfiguration[]>(after.RootElement.GetProperty("Configuration"))!;
        var oldEquity=JsonSerializer.Deserialize<EquityHoldingRow[]>(before.RootElement.GetProperty("Equity"))!;
        var newEquity=JsonSerializer.Deserialize<EquityHoldingRow[]>(after.RootElement.GetProperty("Equity"))!;
        var configurationsEqual=oldConfiguration.Length==newConfiguration.Length&&oldConfiguration.ToHashSet().SetEquals(newConfiguration);
        var projectionUnchanged=oldEquity.Length==newEquity.Length&&oldEquity.ToHashSet().SetEquals(newEquity);
        var oldPlans=JsonSerializer.Deserialize<CRS.Worker.Abstractions.TaskDefinition[]>(before.RootElement.GetProperty("Jobs"))!;
        var newPlans=JsonSerializer.Deserialize<CRS.Worker.Abstractions.TaskDefinition[]>(after.RootElement.GetProperty("Jobs"))!;
        // 计划配置必须逐项保留；退避和失败计数属于恢复后可继续变化的运行状态。
        static object Plan(CRS.Worker.Abstractions.TaskDefinition row)=>new{row.PluginId,row.JobId,row.Cron,row.TimeZoneId,row.Enabled,row.Revision};
        var plansUnchanged=oldPlans.Length==newPlans.Length&&oldPlans.Select(Plan).ToHashSet().SetEquals(newPlans.Select(Plan));
        var oldHistory=JsonSerializer.Deserialize<CRS.Worker.Abstractions.ExecutionRecord[]>(before.RootElement.GetProperty("History"))!;
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));var pipe=new BackgroundPipeClient();var readbacks=new List<object>();var retained=true;
        foreach(var old in oldHistory)
        {
            var reply=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:old.ExecutionId),deadline.Token);
            var row=reply.Execution;
            // 旧在途执行允许进入终态；已经结束的事实不能被新执行覆盖或改写。
            var exact=row is not null&&row.ExecutionId==old.ExecutionId&&row.PluginId==old.PluginId&&row.JobId==old.JobId&&row.ScheduledUtc==old.ScheduledUtc
                &&row.StartedUtc==old.StartedUtc&&row.Attempt==old.Attempt&&(!old.EndedUtc.HasValue||row.EndedUtc==old.EndedUtc&&row.Result==old.Result&&row.Checkpoint==old.Checkpoint);
            retained&=exact;readbacks.Add(new{old.ExecutionId,Matched=exact,Before=old,After=row});
        }
        var health=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),deadline.Token);
        var expectedPid=after.RootElement.GetProperty("Health").GetProperty("ProcessId").GetInt32();var stable=health.Health is{State:"Running"}&&health.Health.ProcessId==expectedPid;
        await Save(afterDirectory,"baseline-comparison.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,Installed=true,ReadOnly=true,
            PlanConfigurationUnchanged=plansUnchanged,BeforePlans=oldPlans.Length,AfterPlans=newPlans.Length,
            ConfigurationUnchanged=configurationsEqual,BeforeConfiguration=oldConfiguration.Length,AfterConfiguration=newConfiguration.Length,
            EquityLatestProjectionUnchanged=projectionUnchanged,BeforeEquity=oldEquity.Length,AfterEquity=newEquity.Length,
            BaselineExecutionsRead=oldHistory.Length,BaselineExecutionsPreserved=retained,HealthStable=stable,Readbacks=readbacks},deadline.Token);
        Console.WriteLine($"COMPARE 计划配置一致={plansUnchanged}，ETF配置一致={configurationsEqual}，股票当前投影一致={projectionUnchanged}，旧执行回读={oldHistory.Length}/{retained}，健康稳定={stable}。");
        // 当前投影可能受恢复后的合法新采集影响；不同不自动推断底层原件丢失，仍保留独立对比结果。
        if(!plansUnchanged||!configurationsEqual||!retained||!stable)Environment.ExitCode=2;
    }
    public static async Task Reports(string directory,bool submit,string ticker="600153",bool etf=false,string market="SSE")
    {
        Directory.CreateDirectory(directory);var path=Path.Combine(directory,"report-request.json");
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=deadline.Token;var pipe=new BackgroundPipeClient();
        var key=new EtfKey(market,ticker);var operation=etf?"etf-report-search":"equity-report-search";
        // 原请求文件一旦落盘不得借同目录换证券或业务；目录仍不是持仓审核。
        if(ticker.Length!=6||!ticker.All(char.IsAsciiDigit)||market is not ("SSE" or "SZSE")||!etf&&market!="SSE")throw new InvalidDataException("仅接受已支持市场的6位证券代码。");
        BackgroundRequest request;
        if(File.Exists(path))request=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(path,token))??throw new InvalidDataException("报告请求损坏。");
        else
        {
            if(!submit)throw new InvalidDataException("没有已保存报告请求，拒绝猜测或创建新任务。");
            var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
            request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team",operation,
                Payload:JsonSerializer.Serialize(new EtfRequest(operation,key.Market,key.Ticker,From:etf?new DateOnly(2025,1,1):today.AddDays(-400),To:today)));
            // 在调用之前记录ID；请求超时后仍只能查询/重试这一ID。
            await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            await JsonSerializer.SerializeAsync(file,request,Json,token);await file.FlushAsync(token);
        }
        var saved=EtfBusinessProtocol.ParseRequest(request.Payload??"");
        if(request.Command!="RunNow"||request.PluginId!="crs.national-team"||request.JobId!=operation||saved.Operation!=operation||saved.Market!=key.Market||saved.Ticker!=key.Ticker)
            throw new InvalidDataException("原报告请求证券或操作不匹配，保留身份并拒绝替换。");
        BackgroundResponse? accepted=null;
        if(submit)
        {
            accepted=await pipe.SendAsync(request,token);
            await Save(directory,"report-submitted.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,request.RequestId,Response=accepted},token);
            Console.WriteLine($"QUEUE {key.Ticker}官方报告目录：{accepted.Code}，request={request.RequestId}；受理不代表已完成。");
        }
        BackgroundResponse command;
        while(true)
        {
            command=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),token);
            if(command.Receipt is null)throw new InvalidDataException("报告命令无持久回执，保留原请求ID，不创建替代请求。");
            if(command.Receipt.State=="Completed"||command.Receipt.Code=="RetryDeferred"||!submit)break;
            await Task.Delay(1000,token);
        }
        BackgroundResponse? execution=null;
        if(command.Receipt?.ExecutionId is {} executionId)
            execution=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:executionId),token);
        // 业务查询失败不能丢掉已经取得的命令/执行终态；目录本身仍须完整重读后才保存。
        await Save(directory,"report-command-status.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,request.RequestId,Command=command,Execution=execution},token);
        var reader=new WorkerEtfWorkspace(pipe);object reports;int count;
        if(etf){var index=await MonitorReadinessTool.Read(()=>reader.EtfReportIndexAsync(key,token),token);reports=index;count=index.Documents.Count;}
        else{var equity=await MonitorReadinessTool.Read(()=>reader.EquityReportsAsync(key,token),token);reports=equity;count=equity.Count;}
        await Save(directory,"report-status.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,request.RequestId,Command=command,Execution=execution,Reports=reports},token);
        Console.WriteLine($"READ 报告目录回执{command.Receipt?.State}/{command.Receipt?.Code}，目录{count}份；仍须独立核验PDF和账户持仓。");
        if(command.Receipt is not {State:"Completed",Code:"Succeeded"})Environment.ExitCode=2;
    }
    public static async Task EtfDocument(string market,string ticker,string url,string directory,bool submit)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=deadline.Token;
        var pipe=new BackgroundPipeClient();var reader=new WorkerEtfWorkspace(pipe);var key=new EtfKey(market,ticker);
        Directory.CreateDirectory(directory);var requestPath=Path.Combine(directory,"document-request.json");
        BackgroundRequest request;
        if(File.Exists(requestPath))request=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(requestPath,token))??throw new InvalidDataException("原件请求损坏。");
        else
        {
            if(!submit)throw new InvalidDataException("没有原件请求，拒绝创建替代身份。");
            // 只登记已由正式后台保存的该基金目录URL；不把研究目录中的本机PDF路径交给服务。
            var index=await reader.EtfReportIndexAsync(key,token);
            if(index.State!="IndexedUnreviewed"||index.Documents.Count(x=>x.DocumentUrl==url)!=1)throw new InvalidDataException("所选原件不在可读的正式ETF目录中。");
            var capability=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",
                Payload:EtfBusinessProtocol.WriteRequest(new("query-capabilities"))),token);
            var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(capability.Payload!);
            var support=JsonSerializer.Deserialize<EtfQueryCapabilities[]>(page.Items)?.SingleOrDefault();
            if(support?.Capabilities.Contains(EtfDocumentCollectionProtocol.Capability,StringComparer.Ordinal)!=true)
                throw new InvalidDataException("后台未声明独立ETF原件采集能力，请先完成后台升级；不会回退管理命令。");
            request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team",EtfDocumentCollectionProtocol.Job(market),
                Payload:EtfBusinessProtocol.WriteRequest(new(EtfDocumentCollectionProtocol.Operation,AlertDocument:new(market,ticker,url))));
            await using var file=new FileStream(requestPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            await JsonSerializer.SerializeAsync(file,request,Json,token);await file.FlushAsync(token);
        }
        var saved=EtfBusinessProtocol.ParseRequest(request.Payload??"");
        // 原目录内的旧持久命令保持原入口；新命令只能经能力协商进入分市场采集。
        var validRoute=request.JobId=="alert-manage"&&saved.Operation=="alert-document"
            ||request.JobId==EtfDocumentCollectionProtocol.Job(market)&&saved.Operation==EtfDocumentCollectionProtocol.Operation;
        if(request.Command!="RunNow"||request.PluginId!="crs.national-team"||!validRoute
            ||saved.AlertDocument is not{} document||document.Market!=market||document.Ticker!=ticker||document.Url!=url)
            throw new InvalidDataException("原件请求身份、证券或URL不匹配。");
        if(submit)
        {
            var response=await pipe.SendAsync(request,token);
            await Save(directory,"document-submitted.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,request.RequestId,Response=response},token);
        }
        BackgroundResponse command;
        while(true)
        {
            command=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),token);
            if(command.Receipt is null)throw new InvalidDataException("原件命令没有持久回执，保留原ID。");
            if(command.Receipt.State=="Completed"||command.Receipt.Code=="RetryDeferred"||!submit)break;
            await Task.Delay(1000,token);
        }
        BackgroundResponse? execution=null;
        if(command.Receipt.ExecutionId is{} executionId)execution=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:executionId),token);
        await Save(directory,"document-command-status.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,request.RequestId,Command=command,Execution=execution},token);
        var documents=await reader.AlertDocumentsAsync(key,token);var registered=documents.SingleOrDefault(x=>x.Url==url);
        EtfHolderCandidateReport? candidates=null;
        if(registered is not null)candidates=await reader.EtfHolderCandidatesAsync(key,registered.Id,token);
        await Save(directory,"document-status.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,request.RequestId,Command=command,Execution=execution,Document=registered,Candidates=candidates},token);
        Console.WriteLine($"DOCUMENT {market}:{ticker} receipt={command.Receipt.State}/{command.Receipt.Code};registered={registered is not null};candidates={candidates?.State}/{candidates?.Holders.Count};holdingsConfirmed=false");
        if(command.Receipt is not{State:"Completed",Code:"Succeeded"}||registered is null)Environment.ExitCode=2;
    }
    private static async Task<BackgroundResponse> Read(BackgroundPipeClient pipe,BackgroundRequest request,CancellationToken token)
    {
        while(true)
        {
            var response=await pipe.SendAsync(request,token);
            if(response.Code is "RateLimited" or "PluginBusy"){await Task.Delay(500,token);continue;}
            if(response.Code!="Ok")throw new InvalidDataException(request.Command+"失败："+response.Code);
            return response;
        }
    }
    private static async Task Save<T>(string directory,string name,T value,CancellationToken token)
    {
        Directory.CreateDirectory(directory);var path=Path.Combine(directory,name);var staging=path+".tmp";
        await File.WriteAllTextAsync(staging,JsonSerializer.Serialize(value,Json),token);token.ThrowIfCancellationRequested();File.Move(staging,path,true);
    }
}
