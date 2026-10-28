using System.Security.Cryptography;
using System.Text.Json;
using CRS.Common.Concurrency;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>独立 Worker 入口；未注入私有存储上下文时拒绝创建临时资料目录。</summary>
public sealed class NationalTeamBackgroundEntry:IContextualBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession()=>throw new InvalidOperationException("需要服务存储上下文。");
    public IBackgroundPluginSession CreateSession(IBackgroundHostContext context)=>new NationalTeamBackgroundSession(context);
}

/// <summary>采集、查询和审核共用一个会话；不持有 WPF、桌面登录态或个人税务密钥。</summary>
public sealed partial class NationalTeamBackgroundSession(IBackgroundHostContext context,IPluginHttpGateway? gateway=null,
    string? databaseProvider=null,NationalTeamStorageProviders? storageProviders=null,IMarketCycleSourceApprovalReader? indexSourceApprovals=null):IBackgroundPluginSession,
    IParameterizedBackgroundSession,IBackgroundQuerySession,IProgressBackgroundSession,IBackgroundQueryDiagnosticsSession,IGroupedBackgroundSession,IBackgroundQueryRoutingSession
{
    public int MaximumConcurrentExecutions=>4;
    public BackgroundExecutionGroup ExecutionGroup(string jobId)
    {
        var job=Operations.Jobs.TryGetValue(jobId,out var handler)?handler.Registration:throw new ArgumentException("未知任务。");
        return new(job.Group,job.MaximumConcurrency);
    }
    private NationalTeamOperationRegistry? operations;
    private NationalTeamOperationRegistry Operations=>LazyInitializer.EnsureInitialized(ref operations,()=>new(this));
    private NationalTeamQueryHandler QueryHandler(string operation)=>Operations.Queries.TryGetValue(operation,out var handler)?handler:throw new ArgumentException("不支持此公开查询。");
    private readonly BoundedWorkAdmission shortQueries=new(2,4,TimeSpan.FromSeconds(1));
    private readonly BoundedWorkAdmission researchQueries=new(1,1,TimeSpan.FromSeconds(2));
    private readonly object cacheGate=new();
    private int buildingSnapshots;
    private IPluginHttpGateway? http=gateway;
    private readonly Dictionary<string,(DateTimeOffset Until,string Request,string[] Rows,string? Metadata)> snapshots=[];
    private readonly Dictionary<string,(DateTimeOffset Until,string Hash,byte[] Bytes)> exports=[];
    private NationalTeamApplication? workspace;
    private byte[]? key;
    private RawEvidenceStore? raw;
    private string rawPath="";
    private sealed class ExecutionProgress(Func<BackgroundProgress,Task>? report)
    {
        public Func<BackgroundProgress,Task>? Report {get;}=report;
        public DateOnly? Date;
        public int HttpStarted;
    }
    private readonly AsyncLocal<ExecutionProgress?> executionProgress=new();
    /// <summary>每次任务独立绑定宿主进度，结束后移除，不能将下一次采集归到旧执行。</summary>
    public async Task<BackgroundJobResult> ExecuteWithProgressAsync(string jobId,string? payload,Func<BackgroundProgress,Task> report,CancellationToken token,Guid? commandId=null)
    {
        return await ExecuteScopedAsync(jobId,payload,token,commandId,report);
    }
    private Task ReportAsync(EtfProgress value)
    {
        var state=executionProgress.Value;if(state is null)return Task.CompletedTask;
        DateOnly? currentDate;
        lock(state)
        {
            if(value.Stage=="RequestStarted")state.HttpStarted++;
            if(value.Date is {} date)state.Date=date;
            currentDate=state.Date;
        }
        // 应用用例的终止阶段映射到宿主已发布的冷却阶段，兼容日志白名单与现有客户端。
        var stage=value.Stage=="SourceCoolingDown"?"Cooldown":value.Stage;
        return state.Report?.Invoke(new(stage,value.Date??currentDate,value.Market,value.Received,value.Added,value.Duplicates,value.Pending,value.HttpStatus,value.Until)
            {Reused=value.Reused,Closed=value.Closed,Empty=value.Empty,Deferred=value.Deferred,Issues=value.Issues})??Task.CompletedTask;
    }

    /// <summary>默认仅自动采集，审核和池变更只接受管理员的持久手动命令。</summary>
    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>Operations.Jobs.Values.OrderBy(x=>x.Registration.Order).Select(x=>x.Registration.Descriptor).ToArray();

    /// <summary>初始化打开公开研究资料库；联网在Quartz作业中执行，不阻塞服务启动。</summary>
    public async ValueTask StartAsync(CancellationToken token)
    {
        if(workspace is not null)throw new InvalidOperationException("会话已经启动。");
        _=Operations;
        var storage=(storageProviders??new NationalTeamStorageProviders()).Create(databaseProvider??NationalTeamStorageProviders.ConfiguredProvider());
        Directory.CreateDirectory(context.DataDirectory);rawPath=Path.Combine(context.DataDirectory,"raw");
        key=context.OpenDataKey();raw=new(rawPath);
        // 来源退避是公开研究状态，使用普通SQLite持久化；密钥仅用于审批审计认证。
        http??=new OfficialHttpGateway(storage.OpenSourcePolicy(Path.Combine(context.DataDirectory,"data")),sources:context as IBackgroundSourceContext);
        if(http is OfficialHttpGateway official)official.Progress=ReportAsync;
        var adapter=new WorkerContext(context.DataDirectory,http);
        workspace=NationalTeamApplicationComposition.Create(adapter,()=>storage.OpenMarket(Path.Combine(context.DataDirectory,"data"),raw,key),raw);
        workspace.SetProgressSink(ReportAsync);workspace.Etf.SetOfficialSource(new OfficialShareSources(http,raw,ReportAsync));await workspace.InitializeAsync(token);
        workspace.Activity.SetMarketActivitySource(new OfficialMarketActivitySource(http));
    }
    public Task<BackgroundJobResult> ExecuteAsync(string jobId,CancellationToken token)=>ExecuteAsync(jobId,null,token);

    private async Task<BackgroundJobResult> ExecuteScopedAsync(string jobId,string? payload,CancellationToken token,Guid? commandId,Func<BackgroundProgress,Task>? report)
    {
        var previous=executionProgress.Value;executionProgress.Value=new(report);
        try{return await ExecuteCoreAsync(jobId,payload,token,commandId);}
        finally{executionProgress.Value=previous;}
    }

    /// <summary>任务经宿主持久命令与执行组进入，数据库操作由workspace短段串行；客户端取消等待不撤销已接受命令。</summary>
    public Task<BackgroundJobResult> ExecuteAsync(string jobId,string? payload,CancellationToken token,Guid? commandId=null)
        =>ExecuteScopedAsync(jobId,payload,token,commandId,null);
    private async Task<BackgroundJobResult> ExecuteCoreAsync(string jobId,string? payload,CancellationToken token,Guid? commandId)
    {
            EtfRequest request;
            try{request=payload is null?new EtfRequest("sync"):Parse(payload);}
            catch(EtfContractException error){return new("Rejected","contract="+error.Code);}
            if(!Operations.Jobs.TryGetValue(jobId,out var handler))throw new InvalidDataException("任务与业务参数不匹配。");
            return await handler.Execute(request,token,commandId);
    }

    /// <summary>写入任务执行检查点：实际 HTTP 开始次数与调用来源适配器次数分开，不能将零行或冷却伪装成下载成功。</summary>
    private string ActivityCheckpoint(MarketActivitySyncReport result) =>
        $"days={result.QueriedDays};sourceCalls={result.RequestedSources};httpStarted={executionProgress.Value?.HttpStarted??0};" +
        $"closedSources={result.ClosedSources};emptySources={result.EmptySources};sourceIssues={result.SourceIssues};" +
        $"rows={result.ReceivedRows};existingSources={result.ExistingSources};firstFailure={result.FirstFailureCode??"None"};"+
        $"failureDate={result.FirstFailureDate:yyyy-MM-dd};failureMarket={result.FirstFailureMarket??"None"};retryAt={result.RetryAt:O};hasMore={result.HasMore}";

    private BackgroundJobResult ActivityResult(MarketActivitySyncReport result)
    {
        var retry=result.RetryAt is {} until?until-CRS.Timing.BeijingClock.Now:
            result.SourceIssues==0&&result.HasMore?TimeSpan.FromSeconds(30):TimeSpan.FromMinutes(5);
        return new(result.SourceIssues>0||result.HasMore?"RetryDeferred":result.ReceivedRows+result.ExistingSources==0?"NotPublished":"Succeeded",
            ActivityCheckpoint(result),retry<TimeSpan.FromSeconds(30)?TimeSpan.FromSeconds(30):retry);
    }

    public string QueryOperation(string payload)
    {
        try
        {
            var operation=Parse(payload).Operation;
            return operation is "status" or "release-query" or "export" or "export-breadth"||Operations.Queries.ContainsKey(operation)?operation:"unknown";
        }
        catch(EtfContractException){return "invalid";}
    }
    public BackgroundQueryRouting DescribeQueryRouting()=>new("Operation","Snapshot",
        Operations.Queries.Select(x=>new BackgroundQueryRoute(x.Key,x.Value.Kind==NationalTeamQueryKind.ShortRead?BackgroundQueryWorkload.ShortRead:BackgroundQueryWorkload.ResearchRead))
            .Concat(new[]{new BackgroundQueryRoute("status",BackgroundQueryWorkload.ShortRead),new("release-query",BackgroundQueryWorkload.ShortRead),
                new("export",BackgroundQueryWorkload.ResearchRead),new("export-breadth",BackgroundQueryWorkload.ResearchRead)}).OrderBy(x=>x.Operation,StringComparer.Ordinal).ToArray());

    /// <summary>只读白名单不含审核、采集、文件写入或任意文件路径，普通用户只能读取公开研究结果。</summary>
    public async Task<string> QueryAsync(string payload,CancellationToken token)
    {
        EtfRequest request;
        try{request=Parse(payload);}
        catch(EtfContractException error){return JsonSerializer.Serialize(new EtfContractFailure(error.Code));}
        // 参数错误在建立/存入快照前拒绝，不能用无效页大小耗尽八份快照额度。
        if(request.Offset<0||request.Count is <1 or >256)throw new ArgumentException("分页参数不正确。");
        if(request.Snapshot is not null&&request.Operation is ("status" or "export" or "export-breadth"))throw new ArgumentException("此操作不接受分页快照。");
        if(request.ResearchRequest is not null&&request.Operation is not ("research-submit" or "research-status" or "research-release"))throw new ArgumentException("此操作不接受嵌套研究请求。");
        var kind=DescribeQueryRouting().Classify(payload);
        using(await (kind==BackgroundQueryWorkload.ShortRead?shortQueries:researchQueries).EnterAsync(token))
        {
            lock(cacheGate)PruneSnapshots();
            if(request.Operation=="status")return JsonSerializer.Serialize(workspace!.Etf.NetworkStatus);
            if(request.Operation=="release-query")
            {
                if(request.Snapshot is {} released)lock(cacheGate)snapshots.Remove(released);
                return JsonSerializer.Serialize("Released");
            }
            if(request.Operation is "export" or "export-breadth")return await ExportAsync(request,token);
            // 页大小只影响传输，不能改变同一快照的金融范围；证券、日期和其他筛选仍完整绑定。
            var canonical=Canonical(request);
            if(request.Snapshot is { } existing)
            {
                lock(cacheGate)
                {
                    if(!snapshots.TryGetValue(existing,out var cached) || cached.Request!=canonical)throw new ArgumentException("查询快照已过期或参数改变，请刷新。");
                    return Page(existing,cached.Rows,cached.Metadata,request);
                }
            }
            var result=await BuildSnapshotAsync(request,token);
            return Page(result.Id,result.Rows,result.Metadata,request);
        }
    }
    private static string Canonical(EtfRequest request)=>JsonSerializer.Serialize(request with{Offset=0,Snapshot=null,Count=10});
    private void PruneSnapshots()
    {
        var now=CRS.Timing.BeijingClock.Now;
        foreach(var id in snapshots.Where(x=>x.Value.Until<now).Select(x=>x.Key).ToArray())snapshots.Remove(id);
        foreach(var id in exports.Where(x=>x.Value.Until<now).Select(x=>x.Key).ToArray())exports.Remove(id);
    }
    private async Task<(string Id,string[] Rows,string? Metadata)> BuildSnapshotAsync(EtfRequest request,CancellationToken token)
    {
        lock(cacheGate)
        {
            PruneSnapshots();if(snapshots.Count+buildingSnapshots>=8)throw new WorkAdmissionException("SnapshotCapacity");buildingSnapshots++;
        }
        try
        {
            var handler=QueryHandler(request.Operation);
            using var scope=ReadWorkloadScope.Enter(handler.Kind==NationalTeamQueryKind.ShortRead);
            var result=await handler.Read(request,token);token.ThrowIfCancellationRequested();
            var rows=result.Rows.Select(row=>{token.ThrowIfCancellationRequested();return BeijingQueryTimes.Normalize(row);}).ToArray();
            var metadata=result.Metadata is {} value?BeijingQueryTimes.Normalize(value):null;
            if(rows.Length>10000||rows.Sum(x=>(long)System.Text.Encoding.UTF8.GetByteCount(x))>2*1024*1024)throw new ArgumentException("查询快照超过限额。");
            var id=Guid.NewGuid().ToString("N");
            lock(cacheGate){token.ThrowIfCancellationRequested();snapshots.Add(id,(CRS.Timing.BeijingClock.Now.AddMinutes(10),Canonical(request),rows,metadata));}
            return(id,rows,metadata);
        }
        finally{lock(cacheGate)buildingSnapshots--;}
    }

    /// <summary>协商后至多256行，仍受原46KB响应限额；同一快照连续分页，不截断或重算金融结果。</summary>
    private static string[] DashboardRows(EtfDashboard dashboard,Func<EtfDashboardRow,string> serialize)=>dashboard.Overview.Select(x=>new EtfDashboardRow(Overview:x))
        .Concat(dashboard.History.Select(x=>new EtfDashboardRow(History:x))).Concat(dashboard.Activity.Select(x=>new EtfDashboardRow(Activity:x))).Select(serialize).ToArray();
    private string Page(string snapshot,string[] rows,string? metadata,EtfRequest request)
    {
        lock(cacheGate)
        {
        if(request.Offset<0 || request.Offset>rows.Length || request.Count is <1 or >256)throw new ArgumentException("分页参数不正确。");
        var count=Math.Min(request.Count,rows.Length-request.Offset);
        while(true)
        {
            var next=request.Offset+count<rows.Length?request.Offset+count:(int?)null;
            var result=JsonSerializer.Serialize(new EtfQueryPage("["+string.Join(',',rows.Skip(request.Offset).Take(count))+"]",snapshot,next,metadata){MaximumPageCount=256,TotalRows=rows.Length});
            if(System.Text.Encoding.UTF8.GetByteCount(result)<=46000){if(next is null)snapshots.Remove(snapshot);return result;}
            if(count<=1){snapshots.Remove(snapshot);throw new ArgumentException("单条记录超过查询响应额度。");}
            // 大行按字节限额缩页；折半避免从256逐行重序列化，下一游标只跨过实际返回的行。
            count=Math.Max(1,count/2);
        }
        }
    }

    /// <summary>只导出登记过且哈希完好的原件；短期字节快照避免分块期间文件变化。</summary>
    private async Task<string> ExportAsync(EtfRequest request,CancellationToken token)
    {
        var id=request.Operation+":"+(request.Revision??"")+":"+request.Original;
        (DateTimeOffset Until,string Hash,byte[] Bytes) cached;
        bool found;lock(cacheGate)found=exports.TryGetValue(id,out cached);
        if(!found)
        {
            lock(cacheGate)if(exports.Count>=2)throw new ArgumentException("导出额度已满。");
            string hash;
            if(request.Operation=="export-breadth")hash=await workspace!.Cycle.MarketBreadthEvidenceAsync(request.Revision??"",request.Original,token);
            else
            {
                var row=(await workspace!.Etf.EvidenceAsync(token)).SingleOrDefault(x=>x.RevisionId==request.Revision)??throw new ArgumentException("证据不存在。");
                if(row.State=="Critical")throw new InvalidDataException("证据损坏。");hash=request.Original?row.OriginalHash:row.Hash;
            }
            var bytes=await raw!.ReadImportFileAsync(Path.Combine(rawPath,hash+".raw"),token);
            if(Convert.ToHexString(SHA256.HashData(bytes))!=hash)throw new InvalidDataException("证据哈希不匹配。");
            cached=(CRS.Timing.BeijingClock.Now.AddMinutes(5),hash,bytes);lock(cacheGate)exports[id]=cached;
        }
        if(request.Offset<0 || request.Offset>cached.Bytes.Length)throw new ArgumentException("证据偏移错误。");
        var chunk=cached.Bytes.Skip(request.Offset).Take(16384).ToArray();
        // 最后一块返回后释放额度；响应丢失时可从不可变原件重建，不能让两次完整导出占满五分钟。
        if(request.Offset+chunk.Length==cached.Bytes.Length)lock(cacheGate)exports.Remove(id);
        return JsonSerializer.Serialize(new EvidenceChunk(cached.Hash,cached.Bytes.Length,request.Offset,chunk));
    }
    /// <summary>JSON只允许有限长度，业务操作另在白名单中验证。</summary>
    private static EtfRequest Parse(string payload)=>EtfBusinessProtocol.ParseRequest(payload);
    private static EtfKey Key(EtfRequest request)=>new(request.Market??"",request.Ticker??"");
    public async ValueTask StopAsync(CancellationToken token)=>await StopQueriesAsync().WaitAsync(token);
    public async ValueTask DisposeAsync()
    {
        await StopQueriesAsync();
        if(workspace is not null)await workspace.DisposeAsync();workspace=null;
        if(key is not null)CryptographicOperations.ZeroMemory(key);key=null;(http as IDisposable)?.Dispose();lock(cacheGate){snapshots.Clear();exports.Clear();foreach(var job in researchJobs.Values)job.Cancellation.Dispose();researchJobs.Clear();}
    }

    /// <summary>复用既有采集用例的最小适配，不暴露桌面调度器和税务服务。</summary>
    private sealed class WorkerContext(string root,IPluginHttpGateway http):IPluginHostContext,IPluginDataPaths,IPluginJobScheduler,IPluginDiagnostics
    {
        public string PluginId=>"crs.national-team";
        public IPluginJobScheduler Jobs=>this;public IPluginDataPaths DataPaths=>this;public IPluginHttpGateway Http=>http;public IPluginDiagnostics Diagnostics=>this;
        public string Data=>Path.Combine(root,"data");public string Raw=>Path.Combine(root,"raw");public string Log=>Path.Combine(root,"log");public string Backup=>Path.Combine(root,"backup");
        public Task RunAsync(string job,Func<CancellationToken,Task> work,CancellationToken token)=>work(token);
        public void Report(string code,string message){ /* 公开任务结果由 Worker 执行记录统一保存。 */ }
    }
}
