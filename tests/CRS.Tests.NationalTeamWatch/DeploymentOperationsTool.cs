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
        var jobs=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetJobs"),token);
        var history=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory","crs.national-team"),token);
        var configuration=await MonitorReadinessTool.Read(()=>reader.ConfigurationAsync(token),token);
        var holdings=await MonitorReadinessTool.Read(()=>reader.EquityHoldingsAsync(null,null,null,false,token),token);
        var after=await Read(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),token);
        if(before.Health is not {State:"Running"}||after.Health is not {State:"Running"}||before.Health.ProcessId!=after.Health.ProcessId||jobs.Jobs is null||history.History is null)
            throw new InvalidDataException("服务状态、进程或正式记录不完整，不生成完整发布状态。");
        await Save(directory,"state.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,Health=after.Health,Jobs=jobs.Jobs,History=history.History,Configuration=configuration,Equity=holdings},token);
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
            ConfigurationUnchanged=configurationsEqual,BeforeConfiguration=oldConfiguration.Length,AfterConfiguration=newConfiguration.Length,
            EquityLatestProjectionUnchanged=projectionUnchanged,BeforeEquity=oldEquity.Length,AfterEquity=newEquity.Length,
            BaselineExecutionsRead=oldHistory.Length,BaselineExecutionsPreserved=retained,HealthStable=stable,Readbacks=readbacks},deadline.Token);
        Console.WriteLine($"COMPARE 配置一致={configurationsEqual}，股票当前投影一致={projectionUnchanged}，旧执行回读={oldHistory.Length}/{retained}，健康稳定={stable}。");
        // 当前投影可能受恢复后的合法新采集影响；不同不自动推断底层原件丢失，仍保留独立对比结果。
        if(!configurationsEqual||!retained||!stable)Environment.ExitCode=2;
    }
    public static async Task Reports(string directory,bool submit,string ticker="600153")
    {
        Directory.CreateDirectory(directory);var path=Path.Combine(directory,"report-request.json");
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=deadline.Token;var pipe=new BackgroundPipeClient();
        var key=new EtfKey("SSE",ticker);
        // 检索只选择已支持的沪市代码，原请求文件一旦落盘不得借同目录换证券；目录仍不是持仓审核。
        if(ticker.Length!=6||!ticker.All(char.IsAsciiDigit))throw new InvalidDataException("股票代码须为6位数字。");
        BackgroundRequest request;
        if(File.Exists(path))request=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(path,token))??throw new InvalidDataException("报告请求损坏。");
        else
        {
            if(!submit)throw new InvalidDataException("没有已保存报告请求，拒绝猜测或创建新任务。");
            var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
            request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","equity-report-search",
                Payload:JsonSerializer.Serialize(new EtfRequest("equity-report-search",key.Market,key.Ticker,From:today.AddDays(-400),To:today)));
            // 在调用之前记录ID；请求超时后仍只能查询/重试这一ID。
            await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            await JsonSerializer.SerializeAsync(file,request,Json,token);await file.FlushAsync(token);
        }
        var saved=EtfBusinessProtocol.ParseRequest(request.Payload??"");
        if(request.JobId!="equity-report-search"||saved.Operation!="equity-report-search"||saved.Market!=key.Market||saved.Ticker!=key.Ticker)
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
        var reports=await MonitorReadinessTool.Read(()=>new WorkerEtfWorkspace(pipe).EquityReportsAsync(key,token),token);
        await Save(directory,"report-status.json",new{CheckedAt=CRS.Timing.BeijingClock.Now,request.RequestId,Command=command,Execution=execution,Reports=reports},token);
        Console.WriteLine($"READ 报告目录回执{command.Receipt?.State}/{command.Receipt?.Code}，目录{reports.Count}份；仍须独立核验PDF和账户持仓。");
        if(command.Receipt is not {State:"Completed",Code:"Succeeded"})Environment.ExitCode=2;
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
