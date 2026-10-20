using System.Globalization;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>真实监控整改留证；查询不审批证据，补采只提交既有Worker作业并保留持久回执。</summary>
internal static class MonitorReadinessTool
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static string Root=>Path.Combine(Environment.CurrentDirectory,"artifacts","monitor-readiness");
    public static async Task Audit()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(20));var token=deadline.Token;
        var workspace=new WorkerEtfWorkspace();var pipe=new BackgroundPipeClient();
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        var metadata=await Read(()=>workspace.AlertMetadataAsync(token),token);var configuration=await Read(()=>workspace.ConfigurationAsync(token),token);
        var pool=configuration.Where(x=>x.Active).OrderBy(x=>x.Market).ThenBy(x=>x.Ticker).ToArray();var acceptance=new List<EtfAcceptance>();
        var plans=await Read(()=>pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetJobs"),token),token);
        var rows=new List<object>();Directory.CreateDirectory(Root);
        var report=new StringBuilder("# 监控与预警逐标的验收\n\n此报告只读真实服务，不审核、不补零、不把补采计入连续采集。\n\n|标的|有效日/250|连续日/20|原件口径|待审修订|最近事实日|\n|---|---:|---:|---|---:|---|\n");
        foreach(var fund in pool)
        {
            var key=new EtfKey(fund.Market,fund.Ticker);
            var item=await Read(()=>AcceptanceForKey(pipe,key,today.AddDays(-1),token),token);acceptance.Add(item);
            var facts=await Read(()=>workspace.EvidenceAsync(key,token),token);
            var pending=facts.Where(x=>x.State is "NeedsReview" or "Critical").ToArray();
            var proof=metadata.SingleOrDefault(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
            var points=await Read(()=>workspace.PointsAsync(key,token),token);var latest=points.LastOrDefault()?.Date;
            var attempts=await Read(()=>workspace.CollectionAttemptsAsync(key,today.AddDays(-7),today,token),token);
            rows.Add(new{Key=key,Acceptance=item,Metadata=proof,PendingEvidence=pending,LatestDate=latest,RecentAttempts=attempts});
            var state=proof is null?"未登记":!proof.OriginalConfirmed?"原件异常":latest is {} d&&proof.Covers(d,d)?"已核验，覆盖最新事实日":"已登记，最新事实日不在核验区间";
            report.AppendLine($"|{key.Market}:{key.Ticker}|{item.AcceptedDays}/{item.ExpectedDays}|{item.ContinuousCollectionDays}/20|{state}|{pending.Length}|{latest:yyyy-MM-dd}|");
            await File.WriteAllTextAsync(Path.Combine(Root,"audit-progress.json"),JsonSerializer.Serialize(new{CheckedAt=DateTimeOffset.UtcNow,Completed=rows.Count,Total=pool.Length,Rows=rows},Json),token);
            Console.WriteLine($"READ 监控核验 {key.Market}:{key.Ticker}：{item.AcceptedDays}/{item.ExpectedDays}，连续{item.ContinuousCollectionDays}/20，异常{pending.Length}，口径{state}");
        }
        report.AppendLine("\n份额历史、连续采集和原件口径分别核验；覆盖最新事实日不代表整个5/20交易日窗口可比。正式金融外发仍沿既有门槛。官方发布时间、来源使用许可、具名ETF黄金样本及实际邮件送达不由此查询确认。");
        await File.WriteAllTextAsync(Path.Combine(Root,"audit.json"),JsonSerializer.Serialize(new{CheckedAt=DateTimeOffset.UtcNow,Acceptance=acceptance,Metadata=metadata,Configuration=configuration,Plans=plans.Jobs,Rows=rows},Json),token);
        await File.WriteAllTextAsync(Path.Combine(Root,"audit.md"),report.ToString(),token);
    }
    /// <summary>单条验收行必须在一页内完整返回，旧服务忽略证券参数时拒绝，不拼接到其他证券。</summary>
    private static async Task<EtfAcceptance> AcceptanceForKey(BackgroundPipeClient pipe,EtfKey key,DateOnly through,CancellationToken token)
    {
        while(true)
        {
            var reply=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",
                Payload:JsonSerializer.Serialize(new EtfRequest("acceptance",key.Market,key.Ticker,To:through))),token);
            if(reply.Code is "PluginBusy" or "RateLimited"){await Task.Delay(500,token);continue;}
            if(reply.Code!="Ok"||reply.Payload is null)throw new InvalidDataException("逐只验收失败："+reply.Code);
            var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(reply.Payload);
            var items=JsonSerializer.Deserialize<List<EtfAcceptance>>(page.Items)??[];
            if(page.NextOffset is not null||items.Count!=1||items[0].Market!=key.Market||items[0].Ticker!=key.Ticker||items[0].Through!=through)
                throw new InvalidDataException("逐只验收响应不匹配，不能把旧服务全池响应当作单证券。");
            return items[0];
        }
    }
    public static async Task Summary()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(10));var token=deadline.Token;
        var workspace=new WorkerEtfWorkspace();var pipe=new BackgroundPipeClient();var rows=new List<EtfAcceptance>();
        var through=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime).AddDays(-1);
        var pool=(await Read(()=>workspace.ConfigurationAsync(token),token)).Where(x=>x.Active).ToArray();
        foreach(var fund in pool)rows.Add(await Read(()=>AcceptanceForKey(pipe,new(fund.Market,fund.Ticker),through,token),token));
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root,"current-acceptance.json"),JsonSerializer.Serialize(new{CheckedAt=DateTimeOffset.UtcNow,Through=through,Acceptance=rows},Json),token);
        Console.WriteLine($"READ 当前覆盖{rows.Count}只，250日通过{rows.Count(x=>x.History250Passed)}只，20日连续通过{rows.Count(x=>x.Continuous20Passed)}只；只读实际事实。");
    }
    /// <summary>仅只读操作允许在服务重启/管道中断后重读，完整重建该次分页；无效响应仍立即失败。</summary>
    internal static async Task<T> Read<T>(Func<Task<T>> query,CancellationToken token)
    {
        for(var attempt=0;;attempt++)
        {
            try{return await query();}
            catch(IOException) when(attempt<4&&!token.IsCancellationRequested)
            {Console.WriteLine("READ 查询中断，退避后重新读取完整结果。");await Task.Delay(TimeSpan.FromSeconds(2<<attempt),token);}
            catch(OperationCanceledException) when(attempt<4&&!token.IsCancellationRequested)
            {Console.WriteLine("READ 单次查询超时，退避后重新读取完整结果。");await Task.Delay(TimeSpan.FromSeconds(2<<attempt),token);}
        }
    }
    /// <summary>只读原请求回执与执行记录；不生成新的补采请求，也不将执行成功等同于覆盖达标。</summary>
    public static async Task BackfillStatus()
    {
        using var saved=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root,"backfill-receipt.json")));
        var id=saved.RootElement.GetProperty("RequestId").GetGuid();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var token=deadline.Token;var pipe=new BackgroundPipeClient();
        var command=await ReadStatus(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:id),token);
        BackgroundResponse? execution=null;
        if(command.Receipt?.ExecutionId is {} executionId)
            execution=await ReadStatus(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:executionId),token);
        var jobs=await ReadStatus(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetJobs"),token);
        var history=await ReadStatus(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory","crs.national-team","official-share-sync"),token);
        await File.WriteAllTextAsync(Path.Combine(Root,"backfill-status.json"),JsonSerializer.Serialize(new{CheckedAt=DateTimeOffset.UtcNow,RequestId=id,
            Command=command,Execution=execution,RecentExecutions=history.History,Plans=jobs.Jobs?.Where(x=>x.PluginId=="crs.national-team"&&x.JobId=="official-share-sync")},Json),token);
        Console.WriteLine($"READ 补采回执：{command.Code} {command.Receipt?.State}/{command.Receipt?.Code}；执行记录已另存，覆盖仍须逐只核验。");
    }
    private static async Task<BackgroundResponse> ReadStatus(BackgroundPipeClient pipe,BackgroundRequest request,CancellationToken token)
    {
        while(true)
        {
            var reply=await pipe.SendAsync(request,token);
            if(reply.Code is "RateLimited" or "PluginBusy"){await Task.Delay(500,token);continue;}
            if(reply.Code!="Ok")throw new InvalidDataException("读取补采状态失败："+request.Command+"/"+reply.Code);
            return reply;
        }
    }
    public static async Task Backfill(string fromText,string throughText)
    {
        var from=DateOnly.ParseExact(fromText,"yyyy-MM-dd",CultureInfo.InvariantCulture);
        var through=DateOnly.ParseExact(throughText,"yyyy-MM-dd",CultureInfo.InvariantCulture);
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        if(from>through||through>=today||through.DayNumber-from.DayNumber>400)throw new InvalidDataException("补采须为400天以内已结束日期。");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var pipe=new BackgroundPipeClient();
        Directory.CreateDirectory(Root);
        // 不循环生成新命令冲刷在途任务；持久回执用于后续区分排队、退避与事实提交。
        var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","official-share-sync",
            Payload:JsonSerializer.Serialize(new EtfRequest("sync",From:from,To:through)));
        var response=await pipe.SendAsync(request,deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(Root,"backfill-receipt.json"),JsonSerializer.Serialize(new{SubmittedAt=DateTimeOffset.UtcNow,From=from,Through=through,RequestId=request.RequestId,response.Code,response.Receipt},Json),deadline.Token);
        Console.WriteLine($"QUEUE 官方份额补采 {from:yyyy-MM-dd}至{through:yyyy-MM-dd}：{response.Code}；这不是历史覆盖完成证明。");
        if(response.Code is not ("Accepted" or "Succeeded" or "RetryDeferred" or "AlreadyRunning"))throw new InvalidDataException("后台未接受补采："+response.Code);
    }
}
