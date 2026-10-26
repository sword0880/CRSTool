using System.Security.Cryptography;
using System.Text.Json;
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
    IParameterizedBackgroundSession,IBackgroundQuerySession,IProgressBackgroundSession,IBackgroundQueryDiagnosticsSession,IGroupedBackgroundSession
{
    public int MaximumConcurrentExecutions=>4;
    public BackgroundExecutionGroup ExecutionGroup(string jobId)=>jobId switch
    {
        "evidence-integrity" or "official-share-sync" or "market-activity-sync" or "official-calendar-sync" or "equity-disclosure-sync"
            or "equity-report-search" or "equity-review" or "equity-history-import" or "nav-sync" or "industry-pe-sync" or "index-series-sync" or "index-valuation-sync"=>new("collection",2),
        "alert-evaluate"=>new("evaluation",1),
        _=>new("management",1)
    };
    private readonly SemaphoreSlim queries=new(1);
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
    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>[
        new("evidence-integrity","证据完整性巡检","0 0/5 * * * ?","China Standard Time",true,true),
        new("official-share-sync","官方ETF份额同步","0 15 23 * * ?","China Standard Time",true,true),
        new("market-activity-sync","沪深股票成交统计同步","0 30 23 * * ?","China Standard Time",true,true),
        new("official-calendar-sync","官方交易日历核验","0 5 23 * * ?","China Standard Time",true,true),
        new("equity-disclosure-sync","直接持股披露检查","0 45 23 * * ?","China Standard Time",true,true),
        new("alert-evaluate","预警评价与通知提交","0 0/5 * * * ?","China Standard Time",true,true),
        new("alert-manage","预警规则与证据管理（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("equity-watch","直接持股观察设置（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("equity-review","直接持股原文核验（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("equity-history-import","历史持股原文登记（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("equity-report-search","查找持股官方报告（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("activity-review","成交统计修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("nav-sync","官方NAV原件采集（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("nav-review","NAV冲突修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("industry-pe-sync","国证行业公布PE采集（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("industry-pe-review","行业公布PE修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("index-series-sync","国证指数日线补缺（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("index-valuation-sync","国证行业指数当期估值（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("index-series-review","指数点位及估值修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("review","ETF修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true),
        new("watchlist","ETF验证池变更（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true)];

    /// <summary>初始化打开公开研究资料库；联网在Quartz作业中执行，不阻塞服务启动。</summary>
    public async ValueTask StartAsync(CancellationToken token)
    {
        if(workspace is not null)throw new InvalidOperationException("会话已经启动。");
        var storage=(storageProviders??new NationalTeamStorageProviders()).Create(databaseProvider??NationalTeamStorageProviders.ConfiguredProvider());
        Directory.CreateDirectory(context.DataDirectory);rawPath=Path.Combine(context.DataDirectory,"raw");
        key=context.OpenDataKey();raw=new(rawPath);
        // 来源退避是公开研究状态，使用普通SQLite持久化；密钥仅用于审批审计认证。
        http??=new OfficialHttpGateway(storage.OpenSourcePolicy(Path.Combine(context.DataDirectory,"data")),sources:context as IBackgroundSourceContext);
        if(http is OfficialHttpGateway official)official.Progress=ReportAsync;
        var adapter=new WorkerContext(context.DataDirectory,http);
        workspace=new(adapter,()=>storage.OpenMarket(Path.Combine(context.DataDirectory,"data"),raw,key),raw);
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
            switch(jobId)
            {
                case "industry-pe-sync": return await SyncIndustryPeAsync(request,token,commandId);
                case "index-series-sync":return await SyncIndexSeriesAsync(request,token,commandId);
                case "index-valuation-sync":return await SyncIndexValuationAsync(request,token,commandId);
                case "index-series-review" when request.Operation=="index-series-review":
                    await workspace!.Index.ReviewIndexSeriesAsync(request.Revision??"",request.Accept,request.Reviewer??"",token,commandId);return BackgroundJobResult.Success();
                case "industry-pe-review" when request.Operation=="industry-pe-review":
                    await workspace!.Industry.ReviewIndustryPeAsync(request.Revision??"",request.Accept,request.Reviewer??"",token,commandId);return BackgroundJobResult.Success("industry=RevisionReviewed");
                case "nav-sync": return await SyncNavAsync(request,token,commandId);
                case "nav-review" when request.Operation=="nav-review":
                    if(request.Market!="SZSE"||request.Ticker!="159915")return new("Rejected","nav=UnsupportedSecurity");
                    try { await workspace!.Nav.ReviewNavAsync(request.Revision??"",request.Accept,request.Reviewer??"",token,commandId);return BackgroundJobResult.Success("nav=RevisionReviewed"); }
                    catch(InvalidDataException) { return new("Rejected","nav=ReviewInvalid"); }
                case "evidence-integrity" when request.Operation is "sync" or "integrity":
                    await workspace!.Evidence.ReconcileEvidenceIntegrityAsync(token);
                    return BackgroundJobResult.Success("integrity=Reconciled");
                case "alert-evaluate" when request.Operation is "sync" or "alert-evaluate":
                    await workspace!.Evidence.ReconcileEvidenceIntegrityAsync(token);
                    await workspace!.Alerts.EvaluateAlertsAsync(request.Operation=="alert-evaluate",token);
                    return await DispatchAlertNotificationsAsync(token);
                case "alert-manage":
                    return await ManageAlertAsync(request,commandId,token);
                case "equity-report-search" when request.Operation=="equity-report-search":
                    return await SearchEquityReportsAsync(request,commandId,token);
                case "activity-review" when request.Operation=="activity-review":
                    await workspace!.Activity.ReviewMarketActivityAsync(request.Revision??"",request.Accept,request.Reviewer??"",token,commandId);return BackgroundJobResult.Success();
                case "equity-watch" when request.Operation=="equity-watch" && request.EquityWatch is {} watch:
                    await workspace!.Equity.SaveEquityWatchAsync(watch,token,commandId);return BackgroundJobResult.Success();
                case "equity-review" when request.Operation is "equity-review" or "equity-review-bound" && request.EquityReview is {} review:
                    if(!Uri.TryCreate(review.DocumentUrl,UriKind.Absolute,out var documentUri)||!OfficialEquityDocuments.IsAllowed(documentUri))return new("Rejected","equity=OfficialPdfRequired");
                    var document=await http!.GetAsync(documentUri,token);
                    if(document.StatusCode!=200)return new("RetryDeferred","equity=DocumentUnavailable",TimeSpan.FromMinutes(30));
                    try{await workspace!.Equity.ReviewEquityDocumentAsync(review,document.Content,token,commandId);}
                    catch(InvalidDataException){return new("Rejected","equity=ReviewInvalid");}
                    return BackgroundJobResult.Success();
                case "equity-history-import" when request.Operation=="equity-history-import":
                    if(commandId is not {} historicalId||!request.Accept||!request.Original||request.Comparable
                        ||string.IsNullOrWhiteSpace(request.Reviewer)||request.Reviewer.Trim().Length>100||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Trim().Length>500)
                        return new("Rejected","equityHistory=ReviewRequired");
                    // 只查固定目录，先检查能力和双回执；重放已提交命令无需再次下载官方原件。
                    var historicalDefinitions=await workspace!.Equity.HistoricalEquityDocumentsAsync(token);
                    var historicalDefinition=historicalDefinitions.SingleOrDefault(x=>x.Id==request.Revision);
                    if(historicalDefinition is null)return new("Rejected","equityHistory=UnknownDefinition");
                    if(await workspace.Equity.HistoricalEquityReceiptAsync(historicalId,token) is {} existingHistorical)
                    {
                        if(existingHistorical.ExtractHash!=historicalDefinition.ExtractHash||!await workspace.Equity.HistoricalEquityRequestMatchesAsync(historicalId,historicalDefinition.Id,request.Reviewer,request.Reason,token))
                            return new("Rejected","equityHistory=RequestBindingChanged");
                        return BackgroundJobResult.Success();
                    }
                    var historicalDocument=await http!.GetAsync(new Uri(historicalDefinition.DocumentUrl),token);
                    if(historicalDocument.StatusCode!=200)return new("RetryDeferred","equityHistory=DocumentUnavailable",TimeSpan.FromMinutes(30));
                    try{await workspace.Equity.ImportHistoricalEquityDocumentAsync(historicalDefinition.Id,historicalDocument.Content,request.Reviewer,request.Reason,historicalId,token);}
                    catch(InvalidDataException){return new("Rejected","equityHistory=EvidenceRejected");}
                    return BackgroundJobResult.Success();
                case "equity-disclosure-sync" when request.Operation is "sync" or "equity-sync":
                    var equityIssues=0;var equityRows=0;
                    foreach(var security in (await workspace!.Equity.EquityWatchlistAsync(token)).Where(x=>x.Active))
                    {
                        try
                        {
                            var sourceUri=OfficialEquitySource.Request(security.Market,security.Ticker);var sourceResponse=await http!.GetAsync(sourceUri,token);
                            if(sourceResponse.StatusCode!=200){equityIssues++;continue;}
                            await workspace.Equity.SaveEquityDisclosureAsync(security.Market,security.Ticker,sourceUri,sourceResponse.Content,token);equityRows++;
                        }
                        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                        catch(Exception error) when(error is InvalidDataException or HttpRequestException or TimeoutException or InvalidOperationException){equityIssues++;}
                    }
                    return new(equityIssues>0?"RetryDeferred":equityRows==0?"NotPublished":"Succeeded",$"equitySources={equityRows};sourceIssues={equityIssues}",TimeSpan.FromMinutes(30));
                case "official-calendar-sync" when request.Operation=="sync":
                    var calendarYear=CRS.Timing.BeijingClock.Now.Year;
                    var calendarIssues=0;
                    foreach(var market in new[]{"SSE","SZSE"})
                    foreach(var year in new[]{calendarYear-1,calendarYear})
                    {
                        try
                        {
                            var uri=ReviewedExchangeCalendar.Source(market,year);var response=await http!.GetAsync(uri,token);
                            if(response.StatusCode!=200){calendarIssues++;continue;}
                            await workspace!.Etf.RegisterCalendarAsync(market,year,response.Content,uri,token);
                        }
                        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                        catch(Exception error) when(error is InvalidDataException or HttpRequestException or TimeoutException or InvalidOperationException){calendarIssues++;}
                    }
                    return calendarIssues==0?BackgroundJobResult.Success("calendar=ReviewedAnnualSchedule"):new("RetryDeferred",$"calendarSourceIssues={calendarIssues}",TimeSpan.FromMinutes(30));
                case "market-activity-sync" when request.Operation is "sync" or "market-sync":
                    var activity=request.From is null && request.To is null?await workspace!.Activity.SyncRecentMarketActivityAsync(token):null;
                    if(activity is null)
                    {
                        if(request.From is null || request.To is null)throw new InvalidDataException("请填写完整成交统计日期区间。");
                        var manualActivity=await workspace!.Activity.SyncMarketActivityReportAsync(request.From.Value,request.To.Value,token);
                        return ActivityResult(manualActivity);
                    }
                    return ActivityResult(activity);
                case "official-share-sync" when request.Operation=="sync":
                    if(!(await workspace!.Etf.ConfigurationAsync(token)).Any(x=>x.Active))return BackgroundJobResult.Success("monitoring=NoActiveEtfs");
                    var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
                    var report=request.From is null && request.To is null?await workspace!.Etf.SyncRecentAsync(token):
                        await workspace!.Etf.SyncAsync(request.From??today.AddDays(-7),request.To??today,token);
                    await workspace!.Alerts.EvaluateAlertsAsync(token);
                    return new(report.SourceIssues>0 || report.HasMore?"RetryDeferred":report.ReceivedRows==0?"NotPublished":"Succeeded",
                        $"days={report.QueriedDays};sourceIssues={report.SourceIssues};rows={report.ReceivedRows}",report.HasMore?TimeSpan.FromSeconds(30):TimeSpan.FromMinutes(30));
                case "review" when request.Operation=="review":
                    await workspace!.Etf.ReviewAsync(request.Revision??"",request.Accept,request.Reviewer??"",request.Comparable,token,commandId);
                    await workspace!.Alerts.EvaluateAlertsAsync(token);break;
                case "watchlist" when request.Operation=="watchlist":
                    if(request.ConfigurationRevision is { } revision)
                    {
                        // 用户输入/并发冲突不等于采集故障，不能让下一次正常配置请求进入故障退避。
                        try{await workspace!.Etf.SaveConfigurationAsync(Key(request),request.Name??"",request.Index??"",request.Active,request.Reason??"",revision,token,commandId);}
                        catch(InvalidDataException){return new("Rejected","configuration=InvalidOrStale");}
                    }
                    else await workspace!.Etf.MembershipAsync(Key(request),request.Name??"",request.Index??"",request.Active,request.Reason??"",token,commandId);
                    break;
                default:throw new InvalidDataException("任务与业务参数不匹配。");
            }
            return BackgroundJobResult.Success();
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
            return operation is "status" or "release-query" or "export" or "dashboard" or "dashboard-profile" or "acceptance-profile" or "overview" or "configuration"
                or "history" or "evidence" or "points" or "nav-points" or "nav-revisions" or "industry-pe-report" or "industry-pe-revisions" or "index-series-report" or "index-series-revisions" or "market-activity" or "activity-revisions" or "collection-attempts" or "acceptance" or "coverage"
                or "equity-watchlist" or "equity-holdings" or "equity-disclosures" or "equity-events" or "equity-reports" or "equity-history-documents" or "equity-history-receipt"
                or "alert-overview" or "alert-configuration" or "alert-events" or "alert-event" or "alert-event-version" or "alert-inputs"
                or "alert-changes" or "alert-outbox" or "alert-deliveries" or "alert-channels" or "alert-documents" or "alert-metadata" or "alert-holdings"
                ?operation:"unknown";
        }
        catch(EtfContractException){return "invalid";}
    }

    /// <summary>只读白名单不含审核、采集、文件写入或任意文件路径，普通用户只能读取公开研究结果。</summary>
    public async Task<string> QueryAsync(string payload,CancellationToken token)
    {
        // 网络采集等待期间仍可查询已提交事实；数据库访问由 workspace 自身串行化。
        var serializedRows=0;var serializedBytes=0;
        string SerializeRow<T>(T value)
        {
            token.ThrowIfCancellationRequested();var row=JsonSerializer.Serialize(value);
            serializedBytes+=System.Text.Encoding.UTF8.GetByteCount(row);
            if(++serializedRows>10000||serializedBytes>2*1024*1024)throw new ArgumentException("查询结果超过限额，请缩小查询范围。");
            return row;
        }
        EtfRequest request;
        try{request=Parse(payload);}
        catch(EtfContractException error){return JsonSerializer.Serialize(new EtfContractFailure(error.Code));}
        // 参数错误在建立/存入快照前拒绝，不能用无效页大小耗尽八份快照额度。
        if(request.Offset<0||request.Count is <1 or >256)throw new ArgumentException("分页参数不正确。");
        await queries.WaitAsync(token);
        try
        {
            foreach(var id in snapshots.Where(x=>x.Value.Until<CRS.Timing.BeijingClock.Now).Select(x=>x.Key).ToArray())snapshots.Remove(id);
            foreach(var id in exports.Where(x=>x.Value.Until<CRS.Timing.BeijingClock.Now).Select(x=>x.Key).ToArray())exports.Remove(id);
            if(request.Operation=="status")return JsonSerializer.Serialize(workspace!.Etf.NetworkStatus);
            if(request.Operation=="release-query")
            {
                if(request.Snapshot is {} released)snapshots.Remove(released);
                return JsonSerializer.Serialize("Released");
            }
            if(request.Operation=="export")return await ExportAsync(request,token);
            // 页大小只影响传输，不能改变同一快照的金融范围；证券、日期和其他筛选仍完整绑定。
            var canonical=JsonSerializer.Serialize(request with{Offset=0,Snapshot=null,Count=10});
            if(request.Snapshot is { } existing)
            {
                if(!snapshots.TryGetValue(existing,out var cached) || cached.Request!=canonical)throw new ArgumentException("查询快照已过期或参数改变，请刷新。");
                return Page(existing,cached.Rows,cached.Metadata,request);
            }
            if(snapshots.Count>=8)throw new ArgumentException("查询快照额度已满，请稍后刷新。");
            string? metadata=null;
            string[] rows;
            switch(request.Operation)
            {
                case "index-series-report":
                    if(request.From is not {} indexFrom||request.To is not {} indexThrough)throw new InvalidDataException("指数研究需要完整日期区间。");
                    var indexReport=await workspace!.Index.IndexSeriesReportAsync(indexFrom,indexThrough,token);
                    indexReport=indexReport with{PriceCollectionAllowed=IndexPriceCollectionAllowed(),ValuationCollectionAllowed=IndexValuationCollectionAllowed()};
                    metadata=JsonSerializer.Serialize(indexReport with{Rows=[]});rows=indexReport.Rows.Select(x=>SerializeRow(x)).ToArray();break;
                case "index-series-revisions":
                    if(request.From is not {} indexRevisionFrom||request.To is not {} indexRevisionThrough)throw new InvalidDataException("指数修订需要完整日期区间。");
                    rows=(await workspace!.Index.IndexSeriesRevisionsAsync(indexRevisionFrom,indexRevisionThrough,token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "industry-pe-report":
                    if(request.From is not {} industryFrom||request.To is not {} industryThrough)throw new InvalidDataException("行业研究需要完整日期区间。");
                    var industry=await workspace!.Industry.IndustryPeReportAsync(industryFrom,industryThrough,token);
                    metadata=JsonSerializer.Serialize(industry with{Rows=[]});rows=industry.Rows.Select(x=>SerializeRow(x)).ToArray();break;
                case "industry-pe-revisions":
                    if(request.From is not {} revisionFrom||request.To is not {} revisionThrough)throw new InvalidDataException("行业修订需要完整日期区间。");
                    rows=(await workspace!.Industry.IndustryPeRevisionsAsync(revisionFrom,revisionThrough,token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "alert-overview":rows=[JsonSerializer.Serialize(await workspace!.Alerts.AlertOverviewAsync(token))];break;
                case "alert-configuration":rows=[JsonSerializer.Serialize(await workspace!.Alerts.AlertConfigurationAsync(token))];break;
                case "alert-events":rows=(await workspace!.Alerts.AlertEventsAsync(request.AlertFilter??new(),token)).Select(x=>SerializeRow(x with{RevisionIds=[],HistoricalRevisionIds=null})).ToArray();break;
                case "alert-event":rows=[JsonSerializer.Serialize((await workspace!.Alerts.AlertEventAsync(request.Revision??"",token)) with{RevisionIds=[],HistoricalRevisionIds=null})];break;
                case "alert-event-version":
                    if(!int.TryParse(request.Name,out var sequence)||sequence<1)throw new InvalidDataException("历史变更序号无效。");
                    rows=[JsonSerializer.Serialize((await workspace!.Alerts.AlertEventVersionAsync(request.Revision??"",sequence,token)) with{RevisionIds=[],HistoricalRevisionIds=null})];break;
                case "alert-inputs":
                    int? inputSequence=request.Name is null?null:int.TryParse(request.Name,out var parsedSequence)&&parsedSequence>0?parsedSequence:throw new InvalidDataException("历史输入版本无效。");
                    rows=(await workspace!.Alerts.AlertInputsAsync(request.Revision??"",inputSequence,token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "alert-changes":rows=(await workspace!.Alerts.AlertChangesAsync(request.Revision??"",token)).Select(x=>SerializeRow(x with{Payload=""})).ToArray();break;
                case "alert-outbox":rows=(request.AlertFilter is null?await workspace!.Alerts.AlertOutboxAsync(token):await workspace!.Alerts.AlertOutboxAsync(request.AlertFilter,token)).Select(x=>SerializeRow(x with{Body=""})).ToArray();break;
                case "alert-deliveries":rows=(await workspace!.Alerts.AlertDeliveriesAsync(request.Revision??"",token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "alert-channels":
                    rows=context is IBackgroundNotificationContext notificationContext?(await notificationContext.NotificationChannelsAsync(token)).Select(x=>JsonSerializer.Serialize(new AlertChannel(x.Id,x.Name,x.Enabled,x.Configured))).ToArray():[];break;
                case "alert-documents":rows=(await workspace!.Alerts.AlertDocumentsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "alert-metadata":rows=(await workspace!.Alerts.AlertMetadataAsync(token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "alert-holdings":rows=(await workspace!.Alerts.NamedEtfHoldingsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "dashboard":
                    if((request.Market is null)!=(request.Ticker is null))throw new ArgumentException("ETF标识不完整。");
                    var dashboard=await workspace!.Etf.DashboardAsync(request.Market is null?null:Key(request),token)
                        ??throw new ArgumentException("存储提供器不支持整页读取。");
                    metadata=JsonSerializer.Serialize(workspace.Etf.NetworkStatus);
                    rows=DashboardRows(dashboard,x=>SerializeRow(x));break;
                case "dashboard-profile":
                    if((request.Market is null)!=(request.Ticker is null))throw new ArgumentException("ETF标识不完整。");
                    var profile=await workspace!.Etf.ProfileDashboardAsync(request.Market is null?null:Key(request),token);
                    var serializationStarted=System.Diagnostics.Stopwatch.GetTimestamp();var serialized=DashboardRows(profile.Dashboard,x=>SerializeRow(x));
                    if(serialized.Length>10000||serialized.Sum(x=>x.Length)>2*1024*1024)throw new ArgumentException("完整查询结果超限，诊断不能绕过正常边界。");
                    var serializationMs=System.Diagnostics.Stopwatch.GetElapsedTime(serializationStarted).TotalMilliseconds;
                    // 使用同一金融读取和逐行序列化路径，只返回有限诊断；不扩大超时、不截断或跳过任何原件。
                    rows=[JsonSerializer.Serialize(new{profile.Stages,RowSerializationMs=serializationMs,FinancialRows=serialized.Length,LogicalCharacters=serialized.Sum(x=>x.Length),
                        DashboardSha256=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile.Dashboard))),
                        Cache="PerQueryOnly",PhysicalColdState="NotControlled"})];break;
                case "overview":rows=(await workspace!.Etf.OverviewAsync(token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "configuration":rows=(await workspace!.Etf.ConfigurationAsync(token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "equity-watchlist":rows=(await workspace!.Equity.EquityWatchlistAsync(token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "equity-holdings":rows=(await workspace!.Equity.EquityHoldingsAsync(request.Search,request.Institution,request.Nature,request.Favorites,token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "equity-disclosures":rows=(await workspace!.Equity.EquityDisclosuresAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "equity-events":rows=(await workspace!.Equity.EquityEventsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "equity-reports":rows=(await workspace!.Equity.EquityReportsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "equity-history-documents":rows=(await workspace!.Equity.HistoricalEquityDocumentsAsync(token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "equity-history-receipt":
                    if(!Guid.TryParseExact(request.Revision,"N",out var historicalReceiptId))throw new ArgumentException("历史回执身份无效。");
                    var historicalReceipt=await workspace!.Equity.HistoricalEquityReceiptAsync(historicalReceiptId,token);
                    rows=historicalReceipt is null?[]:[SerializeRow(historicalReceipt)];break;
                case "evidence":
                    // 按证券过滤必须发生在快照分页和容量校验之前。
                    if((request.Market is null)!=(request.Ticker is null))throw new ArgumentException("ETF标识不完整。");
                    rows=(await workspace!.Etf.EvidenceAsync(request.Market is null?null:Key(request),request.From,request.To,token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "history":
                    if((request.Market is null)!=(request.Ticker is null))throw new ArgumentException("ETF标识不完整。");
                    rows=(await workspace!.Etf.HistoryAsync(request.Market is null?null:Key(request),token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "market-activity":rows=(await workspace!.Activity.MarketActivityAsync(token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "activity-revisions":rows=(await workspace!.Activity.MarketActivityRevisionsAsync(token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "points":rows=(await workspace!.Etf.PointsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "nav-points":
                case "nav-revisions":
                    // NAV仍按显式日期有界读取，避免多年历史分页耗尽快照期限后返回半份结果。
                    if(request.From is null||request.To is null||request.To<request.From||request.To.Value.DayNumber-request.From.Value.DayNumber>400)throw new InvalidDataException("NAV查询区间必须不超过400个自然日。");
                    rows=request.Operation=="nav-points"?(await workspace!.Nav.NavPointsAsync(Key(request),request.From.Value,request.To.Value,token)).Select(x=>SerializeRow(x)).ToArray()
                        :(await workspace!.Nav.NavRevisionsAsync(Key(request),request.From.Value,request.To.Value,token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "collection-attempts":
                    if(request.From is null||request.To is null||request.To<request.From||request.To.Value.DayNumber-request.From.Value.DayNumber>400)throw new InvalidDataException("采集查询区间必须不超过400个自然日。");
                    rows=(await workspace!.Etf.CollectionAttemptsAsync(Key(request),request.From.Value,request.To.Value,token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "acceptance-profile":
                    if(request.To is null||(request.Market is null)!=(request.Ticker is null))throw new InvalidDataException("验收诊断需要截至日期及完整ETF标识。");
                    var acceptanceProfile=await workspace!.Etf.ProfileAcceptanceAsync(request.Market is null?null:Key(request),request.To.Value,token);
                    rows=[JsonSerializer.Serialize(acceptanceProfile)];break;
                case "acceptance":
                    if(request.To is null)throw new InvalidDataException("验收需要截至日期。");
                    rows=(request.Market is null&&request.Ticker is null?await workspace!.Etf.AcceptanceAsync(request.To.Value,token)
                        :await workspace!.Etf.AcceptanceAsync(Key(request),request.To.Value,token)).Select(x=>SerializeRow(x)).ToArray();break;
                case "coverage":
                    if(request.From is null || request.To is null || request.To<request.From || request.To.Value.DayNumber-request.From.Value.DayNumber>400)throw new ArgumentException("覆盖区间超限。");
                    var coverage=await workspace!.Etf.CoverageAsync(Key(request),request.From.Value,request.To.Value,token);
                    metadata=JsonSerializer.Serialize(coverage with{Days=[]});rows=coverage.Days.Select(x=>SerializeRow(x)).ToArray();break;
                default:throw new ArgumentException("不支持此公开查询。");
            }
            token.ThrowIfCancellationRequested();
            if(rows.Length>10000 || rows.Sum(x=>x.Length)>2*1024*1024 || snapshots.Count>=8)throw new ArgumentException("查询快照额度已满，请稍后刷新。");
            rows=rows.Select(row=>{token.ThrowIfCancellationRequested();return BeijingQueryTimes.Normalize(row);}).ToArray();
            if(metadata is not null)metadata=BeijingQueryTimes.Normalize(metadata);
            if(rows.Sum(x=>x.Length)>2*1024*1024)throw new ArgumentException("查询快照超过时间元数据归一后的限额。");
            token.ThrowIfCancellationRequested();
            var snapshot=Guid.NewGuid().ToString("N");snapshots.Add(snapshot,(CRS.Timing.BeijingClock.Now.AddMinutes(10),canonical,rows,metadata));
            return Page(snapshot,rows,metadata,request);
        }
        finally{queries.Release();}
    }

    /// <summary>协商后至多256行，仍受原46KB响应限额；同一快照连续分页，不截断或重算金融结果。</summary>
    private static string[] DashboardRows(EtfDashboard dashboard,Func<EtfDashboardRow,string> serialize)=>dashboard.Overview.Select(x=>new EtfDashboardRow(Overview:x))
        .Concat(dashboard.History.Select(x=>new EtfDashboardRow(History:x))).Concat(dashboard.Activity.Select(x=>new EtfDashboardRow(Activity:x))).Select(serialize).ToArray();
    private string Page(string snapshot,string[] rows,string? metadata,EtfRequest request)
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

    /// <summary>只导出登记过且哈希完好的原件；短期字节快照避免分块期间文件变化。</summary>
    private async Task<string> ExportAsync(EtfRequest request,CancellationToken token)
    {
        var id=(request.Revision??"")+":"+request.Original;
        if(!exports.TryGetValue(id,out var cached))
        {
            if(exports.Count>=2)throw new ArgumentException("导出额度已满。");
            var row=(await workspace!.Etf.EvidenceAsync(token)).SingleOrDefault(x=>x.RevisionId==request.Revision)
                ??throw new ArgumentException("证据不存在。");
            if(row.State=="Critical")throw new InvalidDataException("证据损坏。");
            var hash=request.Original?row.OriginalHash:row.Hash;
            var bytes=await raw!.ReadImportFileAsync(Path.Combine(rawPath,hash+".raw"),token);
            if(Convert.ToHexString(SHA256.HashData(bytes))!=hash)throw new InvalidDataException("证据哈希不匹配。");
            cached=(CRS.Timing.BeijingClock.Now.AddMinutes(5),hash,bytes);exports[id]=cached;
        }
        if(request.Offset<0 || request.Offset>cached.Bytes.Length)throw new ArgumentException("证据偏移错误。");
        var chunk=cached.Bytes.Skip(request.Offset).Take(16384).ToArray();
        // 最后一块返回后释放额度；响应丢失时可从不可变原件重建，不能让两次完整导出占满五分钟。
        if(request.Offset+chunk.Length==cached.Bytes.Length)exports.Remove(id);
        return JsonSerializer.Serialize(new EvidenceChunk(cached.Hash,cached.Bytes.Length,request.Offset,chunk));
    }
    /// <summary>JSON只允许有限长度，业务操作另在白名单中验证。</summary>
    private static EtfRequest Parse(string payload)=>EtfBusinessProtocol.ParseRequest(payload);
    private static EtfKey Key(EtfRequest request)=>new(request.Market??"",request.Ticker??"");
    public ValueTask StopAsync(CancellationToken token)=>ValueTask.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        if(workspace is not null)await workspace.DisposeAsync();workspace=null;
        if(key is not null)CryptographicOperations.ZeroMemory(key);key=null;(http as IDisposable)?.Dispose();queries.Dispose();snapshots.Clear();exports.Clear();
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
