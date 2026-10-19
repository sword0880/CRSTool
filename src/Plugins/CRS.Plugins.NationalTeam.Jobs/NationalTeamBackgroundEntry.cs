using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>独立 Worker 入口；未注入私有存储上下文时拒绝创建临时或明文库。</summary>
public sealed class NationalTeamBackgroundEntry:IContextualBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession()=>throw new InvalidOperationException("需要服务存储上下文。");
    public IBackgroundPluginSession CreateSession(IBackgroundHostContext context)=>new NationalTeamBackgroundSession(context);
}

/// <summary>采集、查询和审核共用一个会话；不持有 WPF、桌面登录态或个人税务密钥。</summary>
public sealed partial class NationalTeamBackgroundSession(IBackgroundHostContext context,IPluginHttpGateway? gateway=null):IBackgroundPluginSession,
    IParameterizedBackgroundSession,IBackgroundQuerySession,IProgressBackgroundSession
{
    private readonly SemaphoreSlim serial=new(1);
    private readonly SemaphoreSlim queries=new(1);
    private IPluginHttpGateway? http=gateway;
    private readonly Dictionary<string,(DateTimeOffset Until,string Request,string[] Rows,string? Metadata)> snapshots=[];
    private readonly Dictionary<string,(DateTimeOffset Until,string Hash,byte[] Bytes)> exports=[];
    private EtfWorkspace? workspace;
    private byte[]? key;
    private RawEvidenceStore? raw;
    private string rawPath="";
    private Func<BackgroundProgress,Task>? progress;
    private DateOnly? progressDate;
    /// <summary>每次任务独立绑定宿主进度，结束后移除，不能将下一次采集归到旧执行。</summary>
    public async Task<BackgroundJobResult> ExecuteWithProgressAsync(string jobId,string? payload,Func<BackgroundProgress,Task> report,CancellationToken token,Guid? commandId=null)
    {
        progress=report;progressDate=null;
        try{return await ExecuteAsync(jobId,payload,token,commandId);}
        finally{progress=null;progressDate=null;}
    }
    private Task ReportAsync(EtfProgress value)
    {
        if(value.Date is { } date)progressDate=date;
        return progress?.Invoke(new(value.Stage,value.Date??progressDate,value.Market,value.Received,value.Added,value.Duplicates,value.Pending,value.HttpStatus,value.Until))??Task.CompletedTask;
    }

    /// <summary>默认仅自动采集，审核和池变更只接受管理员的持久手动命令。</summary>
    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>[
        new("official-share-sync","官方ETF份额同步","0 15 23 * * ?","China Standard Time",true,true),
        new("market-activity-sync","沪深股票成交统计同步","0 30 23 * * ?","China Standard Time",true,true),
        new("official-calendar-sync","官方交易日历核验","0 5 23 * * ?","China Standard Time",true,true),
        new("equity-disclosure-sync","直接持股披露检查","0 45 23 * * ?","China Standard Time",true,true),
        new("alert-evaluate","预警评价与通知提交","0 0/5 * * * ?","China Standard Time",true,true),
        new("alert-manage","预警规则与证据管理（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("equity-watch","直接持股观察设置（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("equity-review","直接持股原文核验（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("equity-report-search","查找持股官方报告（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("activity-review","成交统计修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("review","ETF修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("watchlist","ETF验证池变更（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true)];

    /// <summary>初始化只打开服务私有加密库；联网在 Quartz 作业中执行，不阻塞服务启动。</summary>
    public async ValueTask StartAsync(CancellationToken token)
    {
        if(workspace is not null)throw new InvalidOperationException("会话已经启动。");
        Directory.CreateDirectory(context.DataDirectory);rawPath=Path.Combine(context.DataDirectory,"raw");
        key=context.OpenDataKey();raw=new(rawPath);
        // 策略库使用同一插件专用密钥和受限目录，缺失密钥或损坏时不能退回内存策略。
        http??=new OfficialHttpGateway(new SourceRequestPolicy(Path.Combine(context.DataDirectory,"data"),key));
        if(http is OfficialHttpGateway official)official.Progress=ReportAsync;
        var adapter=new WorkerContext(context.DataDirectory,http);
        workspace=new(adapter,()=>new MarketRepository(Path.Combine(context.DataDirectory,"data"),raw,encryptionKey:key),raw);
        workspace.SetProgressSink(ReportAsync);workspace.SetOfficialSource(new OfficialShareSources(http,raw,ReportAsync));await workspace.InitializeAsync(token);
        workspace.SetMarketActivitySource(new OfficialMarketActivitySource(http));
    }
    public Task<BackgroundJobResult> ExecuteAsync(string jobId,CancellationToken token)=>ExecuteAsync(jobId,null,token);

    /// <summary>所有写操作经宿主持久命令与插件串行门槛进入；客户端取消等待不撤销已接受命令。</summary>
    public async Task<BackgroundJobResult> ExecuteAsync(string jobId,string? payload,CancellationToken token,Guid? commandId=null)
    {
        // 与宿主保持同一插件写任务串行，避免采集、配置和审核交错提交。
        await serial.WaitAsync(token);
        try
        {
            var request=payload is null?new EtfRequest("sync"):Parse(payload);
            switch(jobId)
            {
                case "alert-evaluate" when request.Operation is "sync" or "alert-evaluate":
                    await workspace!.EvaluateAlertsAsync(request.Operation=="alert-evaluate",token);
                    return await DispatchAlertNotificationsAsync(token);
                case "alert-manage":
                    return await ManageAlertAsync(request,commandId,token);
                case "equity-report-search" when request.Operation=="equity-report-search":
                    return await SearchEquityReportsAsync(request,commandId,token);
                case "activity-review" when request.Operation=="activity-review":
                    await workspace!.ReviewMarketActivityAsync(request.Revision??"",request.Accept,request.Reviewer??"",token,commandId);return BackgroundJobResult.Success();
                case "equity-watch" when request.Operation=="equity-watch" && request.EquityWatch is {} watch:
                    await workspace!.SaveEquityWatchAsync(watch,token,commandId);return BackgroundJobResult.Success();
                case "equity-review" when request.Operation=="equity-review" && request.EquityReview is {} review:
                    if(!Uri.TryCreate(review.DocumentUrl,UriKind.Absolute,out var documentUri)||!OfficialEquityDocuments.IsAllowed(documentUri))return new("Rejected","equity=OfficialPdfRequired");
                    var document=await http!.GetAsync(documentUri,token);
                    if(document.StatusCode!=200)return new("RetryDeferred","equity=DocumentUnavailable",TimeSpan.FromMinutes(30));
                    try{await workspace!.ReviewEquityDocumentAsync(review,document.Content,token,commandId);}
                    catch(InvalidDataException){return new("Rejected","equity=ReviewInvalid");}
                    return BackgroundJobResult.Success();
                case "equity-disclosure-sync" when request.Operation is "sync" or "equity-sync":
                    var equityIssues=0;var equityRows=0;
                    foreach(var security in (await workspace!.EquityWatchlistAsync(token)).Where(x=>x.Active))
                    {
                        try
                        {
                            var sourceUri=OfficialEquitySource.Request(security.Market,security.Ticker);var sourceResponse=await http!.GetAsync(sourceUri,token);
                            if(sourceResponse.StatusCode!=200){equityIssues++;continue;}
                            await workspace.SaveEquityDisclosureAsync(security.Market,security.Ticker,sourceUri,sourceResponse.Content,token);equityRows++;
                        }
                        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                        catch(Exception error) when(error is InvalidDataException or HttpRequestException or TimeoutException or InvalidOperationException){equityIssues++;}
                    }
                    return new(equityIssues>0?"RetryDeferred":equityRows==0?"NotPublished":"Succeeded",$"equitySources={equityRows};sourceIssues={equityIssues}",TimeSpan.FromMinutes(30));
                case "official-calendar-sync" when request.Operation=="sync":
                    var calendarYear=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").Year;
                    var calendarIssues=0;
                    foreach(var market in new[]{"SSE","SZSE"})
                    foreach(var year in new[]{calendarYear-1,calendarYear})
                    {
                        try
                        {
                            var uri=ReviewedExchangeCalendar.Source(market,year);var response=await http!.GetAsync(uri,token);
                            if(response.StatusCode!=200){calendarIssues++;continue;}
                            await workspace!.RegisterCalendarAsync(market,year,response.Content,uri,token);
                        }
                        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                        catch(Exception error) when(error is InvalidDataException or HttpRequestException or TimeoutException or InvalidOperationException){calendarIssues++;}
                    }
                    return calendarIssues==0?BackgroundJobResult.Success("calendar=ReviewedAnnualSchedule"):new("RetryDeferred",$"calendarSourceIssues={calendarIssues}",TimeSpan.FromMinutes(30));
                case "market-activity-sync" when request.Operation is "sync" or "market-sync":
                    var activity=request.From is null && request.To is null?await workspace!.SyncRecentMarketActivityAsync(token):null;
                    if(activity is null)
                    {
                        if(request.From is null || request.To is null)throw new InvalidDataException("请填写完整成交统计日期区间。");
                        var manualActivity=await workspace!.SyncMarketActivityReportAsync(request.From.Value,request.To.Value,token);
                        return new(manualActivity.SourceIssues>0?"RetryDeferred":manualActivity.ReceivedRows==0?"NotPublished":"Succeeded",
                            $"days={manualActivity.QueriedDays};sourceIssues={manualActivity.SourceIssues};rows={manualActivity.ReceivedRows}",TimeSpan.FromMinutes(30));
                    }
                    return new(activity.SourceIssues>0 || activity.HasMore?"RetryDeferred":activity.ReceivedRows==0?"NotPublished":"Succeeded",
                        $"days={activity.QueriedDays};sourceIssues={activity.SourceIssues};rows={activity.ReceivedRows}",activity.SourceIssues==0 && activity.HasMore?TimeSpan.FromSeconds(30):TimeSpan.FromMinutes(30));
                case "official-share-sync" when request.Operation=="sync":
                    if(!(await workspace!.ConfigurationAsync(token)).Any(x=>x.Active))return BackgroundJobResult.Success("monitoring=NoActiveEtfs");
                    var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
                    var report=request.From is null && request.To is null?await workspace!.SyncRecentAsync(token):
                        await workspace!.SyncAsync(request.From??today.AddDays(-7),request.To??today,token);
                    await workspace!.EvaluateAlertsAsync(token);
                    return new(report.SourceIssues>0 || report.HasMore?"RetryDeferred":report.ReceivedRows==0?"NotPublished":"Succeeded",
                        $"days={report.QueriedDays};sourceIssues={report.SourceIssues};rows={report.ReceivedRows}",report.HasMore?TimeSpan.FromSeconds(30):TimeSpan.FromMinutes(30));
                case "review" when request.Operation=="review":
                    await workspace!.ReviewAsync(request.Revision??"",request.Accept,request.Reviewer??"",request.Comparable,token,commandId);
                    await workspace!.EvaluateAlertsAsync(token);break;
                case "watchlist" when request.Operation=="watchlist":
                    if(request.ConfigurationRevision is { } revision)
                    {
                        // 用户输入/并发冲突不等于采集故障，不能让下一次正常配置请求进入故障退避。
                        try{await workspace!.SaveConfigurationAsync(Key(request),request.Name??"",request.Index??"",request.Active,request.Reason??"",revision,token,commandId);}
                        catch(InvalidDataException){return new("Rejected","configuration=InvalidOrStale");}
                    }
                    else await workspace!.MembershipAsync(Key(request),request.Name??"",request.Index??"",request.Active,request.Reason??"",token,commandId);
                    break;
                default:throw new InvalidDataException("任务与业务参数不匹配。");
            }
            return BackgroundJobResult.Success();
        }
        finally{serial.Release();}
    }

    /// <summary>只读白名单不含审核、采集、文件写入或任意文件路径，普通用户只能读取公开研究结果。</summary>
    public async Task<string> QueryAsync(string payload,CancellationToken token)
    {
        // 网络采集等待期间仍可查询已提交事实；数据库访问由 workspace 自身串行化。
        var request=Parse(payload);await queries.WaitAsync(token);
        try
        {
            foreach(var id in snapshots.Where(x=>x.Value.Until<DateTimeOffset.UtcNow).Select(x=>x.Key).ToArray())snapshots.Remove(id);
            foreach(var id in exports.Where(x=>x.Value.Until<DateTimeOffset.UtcNow).Select(x=>x.Key).ToArray())exports.Remove(id);
            if(request.Operation=="status")return JsonSerializer.Serialize(workspace!.NetworkStatus);
            if(request.Operation=="export")return await ExportAsync(request,token);
            var canonical=JsonSerializer.Serialize(request with{Offset=0,Snapshot=null});
            if(request.Snapshot is { } existing)
            {
                if(!snapshots.TryGetValue(existing,out var cached) || cached.Request!=canonical)throw new ArgumentException("查询快照已过期或参数改变，请刷新。");
                return Page(existing,cached.Rows,cached.Metadata,request);
            }
            string? metadata=null;
            string[] rows;
            switch(request.Operation)
            {
                case "alert-overview":rows=[JsonSerializer.Serialize(await workspace!.AlertOverviewAsync(token))];break;
                case "alert-configuration":rows=[JsonSerializer.Serialize(await workspace!.AlertConfigurationAsync(token))];break;
                case "alert-events":rows=(await workspace!.AlertEventsAsync(request.AlertFilter??new(),token)).Select(x=>JsonSerializer.Serialize(x with{RevisionIds=[]})).ToArray();break;
                case "alert-event":rows=[JsonSerializer.Serialize(await workspace!.AlertEventAsync(request.Revision??"",token))];break;
                case "alert-changes":rows=(await workspace!.AlertChangesAsync(request.Revision??"",token)).Select(x=>JsonSerializer.Serialize(x with{Payload=""})).ToArray();break;
                case "alert-outbox":rows=(request.AlertFilter is null?await workspace!.AlertOutboxAsync(token):await workspace!.AlertOutboxAsync(request.AlertFilter,token)).Select(x=>JsonSerializer.Serialize(x with{Body=""})).ToArray();break;
                case "alert-deliveries":rows=(await workspace!.AlertDeliveriesAsync(request.Revision??"",token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "alert-channels":
                    rows=context is IBackgroundNotificationContext notificationContext?(await notificationContext.NotificationChannelsAsync(token)).Select(x=>JsonSerializer.Serialize(new AlertChannel(x.Id,x.Name,x.Enabled,x.Configured))).ToArray():[];break;
                case "alert-documents":rows=(await workspace!.AlertDocumentsAsync(Key(request),token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "alert-metadata":rows=(await workspace!.AlertMetadataAsync(token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "alert-holdings":rows=(await workspace!.NamedEtfHoldingsAsync(Key(request),token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "overview":rows=(await workspace!.OverviewAsync(token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "configuration":rows=(await workspace!.ConfigurationAsync(token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "equity-watchlist":rows=(await workspace!.EquityWatchlistAsync(token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "equity-holdings":rows=(await workspace!.EquityHoldingsAsync(request.Search,request.Institution,request.Nature,request.Favorites,token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "equity-disclosures":rows=(await workspace!.EquityDisclosuresAsync(Key(request),token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "equity-events":rows=(await workspace!.EquityEventsAsync(Key(request),token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "equity-reports":rows=(await workspace!.EquityReportsAsync(Key(request),token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "evidence":
                    // 按证券过滤必须发生在快照分页和容量校验之前。
                    rows=(request.Market is null&&request.Ticker is null?await workspace!.EvidenceAsync(token):await workspace!.EvidenceAsync(Key(request),token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "history":
                    if((request.Market is null)!=(request.Ticker is null))throw new ArgumentException("ETF标识不完整。");
                    rows=(await workspace!.HistoryAsync(request.Market is null?null:Key(request),token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "market-activity":rows=(await workspace!.MarketActivityAsync(token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "activity-revisions":rows=(await workspace!.MarketActivityRevisionsAsync(token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "points":rows=(await workspace!.PointsAsync(Key(request),token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "collection-attempts":
                    if(request.From is null||request.To is null||request.To<request.From||request.To.Value.DayNumber-request.From.Value.DayNumber>400)throw new InvalidDataException("采集查询区间必须不超过400个自然日。");
                    rows=(await workspace!.CollectionAttemptsAsync(Key(request),request.From.Value,request.To.Value,token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "acceptance":
                    if(request.To is null)throw new InvalidDataException("验收需要截至日期。");
                    rows=(await workspace!.AcceptanceAsync(request.To.Value,token)).Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                case "coverage":
                    if(request.From is null || request.To is null || request.To<request.From || request.To.Value.DayNumber-request.From.Value.DayNumber>400)throw new ArgumentException("覆盖区间超限。");
                    var coverage=await workspace!.CoverageAsync(Key(request),request.From.Value,request.To.Value,token);
                    metadata=JsonSerializer.Serialize(coverage with{Days=[]});rows=coverage.Days.Select(x=>JsonSerializer.Serialize(x)).ToArray();break;
                default:throw new ArgumentException("不支持此公开查询。");
            }
            if(rows.Length>10000 || rows.Sum(x=>x.Length)>2*1024*1024 || snapshots.Count>=8)throw new ArgumentException("查询快照额度已满，请稍后刷新。");
            var snapshot=Guid.NewGuid().ToString("N");snapshots.Add(snapshot,(DateTimeOffset.UtcNow.AddMinutes(10),canonical,rows,metadata));
            return Page(snapshot,rows,metadata,request);
        }
        finally{queries.Release();}
    }

    /// <summary>分页每次至多十行，沿用同一份数据快照，读完后释放服务端缓存。</summary>
    private string Page(string snapshot,string[] rows,string? metadata,EtfRequest request)
    {
        if(request.Offset<0 || request.Offset>rows.Length || request.Count is <1 or >10)throw new ArgumentException("分页参数不正确。");
        var count=Math.Min(request.Count,rows.Length-request.Offset);
        while(true)
        {
            var next=request.Offset+count<rows.Length?request.Offset+count:(int?)null;
            var result=JsonSerializer.Serialize(new EtfQueryPage("["+string.Join(',',rows.Skip(request.Offset).Take(count))+"]",snapshot,next,metadata));
            if(System.Text.Encoding.UTF8.GetByteCount(result)<=46000){if(next is null)snapshots.Remove(snapshot);return result;}
            if(count<=1){snapshots.Remove(snapshot);throw new ArgumentException("单条记录超过查询响应额度。");}
            count--;
        }
    }

    /// <summary>只导出登记过且哈希完好的原件；短期字节快照避免分块期间文件变化。</summary>
    private async Task<string> ExportAsync(EtfRequest request,CancellationToken token)
    {
        var id=(request.Revision??"")+":"+request.Original;
        if(!exports.TryGetValue(id,out var cached))
        {
            if(exports.Count>=2)throw new ArgumentException("导出额度已满。");
            var row=(await workspace!.EvidenceAsync(token)).SingleOrDefault(x=>x.RevisionId==request.Revision)
                ??throw new ArgumentException("证据不存在。");
            if(row.State=="Critical")throw new InvalidDataException("证据损坏。");
            var hash=request.Original?row.OriginalHash:row.Hash;
            var bytes=await raw!.ReadImportFileAsync(Path.Combine(rawPath,hash+".raw"),token);
            if(Convert.ToHexString(SHA256.HashData(bytes))!=hash)throw new InvalidDataException("证据哈希不匹配。");
            cached=(DateTimeOffset.UtcNow.AddMinutes(5),hash,bytes);exports[id]=cached;
        }
        if(request.Offset<0 || request.Offset>cached.Bytes.Length)throw new ArgumentException("证据偏移错误。");
        var chunk=cached.Bytes.Skip(request.Offset).Take(16384).ToArray();
        // 最后一块返回后释放额度；响应丢失时可从不可变原件重建，不能让两次完整导出占满五分钟。
        if(request.Offset+chunk.Length==cached.Bytes.Length)exports.Remove(id);
        return JsonSerializer.Serialize(new EvidenceChunk(cached.Hash,cached.Bytes.Length,request.Offset,chunk));
    }
    /// <summary>JSON只允许有限长度，业务操作另在白名单中验证。</summary>
    private static EtfRequest Parse(string payload)=>payload.Length<=4096?JsonSerializer.Deserialize<EtfRequest>(payload)
        ??throw new ArgumentException("业务参数为空。"):throw new ArgumentException("业务参数过长。");
    private static EtfKey Key(EtfRequest request)=>new(request.Market??"",request.Ticker??"");
    public ValueTask StopAsync(CancellationToken token)=>ValueTask.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        if(workspace is not null)await workspace.DisposeAsync();workspace=null;
        if(key is not null)CryptographicOperations.ZeroMemory(key);key=null;(http as IDisposable)?.Dispose();serial.Dispose();queries.Dispose();snapshots.Clear();exports.Clear();
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
