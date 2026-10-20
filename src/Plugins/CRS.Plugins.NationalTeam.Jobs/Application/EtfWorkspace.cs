using System.Text;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
/// <summary>插件应用用例：串行访问私有仓储，在后台执行导入、查询与证据核验。</summary>
public sealed partial class EtfWorkspace : IEtfWorkspace,IAsyncDisposable
{
    private readonly IPluginHostContext context;
    private readonly SemaphoreSlim gate=new(1);
    private readonly IRawEvidenceStore raw;
    private readonly Func<IMarketRepository> repositoryFactory;
    private IMarketRepository? repository;
    private IOfficialShareSource? officialSource;
    private readonly SemaphoreSlim syncGate=new(1);
    private Func<EtfProgress,Task>? progress;
    public void SetProgressSink(Func<EtfProgress,Task>? sink)=>progress=sink;
    private Task ReportAsync(EtfProgress value)=>progress?.Invoke(value)??Task.CompletedTask;
    public string NetworkStatus {get;private set;}="等待后台服务采集；关闭或锁定桌面不影响任务运行。";
    public void SetOfficialSource(IOfficialShareSource source)=>officialSource=source;
    public EtfWorkspace(IPluginHostContext context,Func<IMarketRepository> repositoryFactory,IRawEvidenceStore raw) {this.context=context;this.repositoryFactory=repositoryFactory;this.raw=raw;}
    public Task InitializeAsync(CancellationToken token)=>RunAsync(()=> { repository=repositoryFactory(); return true; },token);
    public Task RegisterCalendarAsync(string market,int year,byte[] bytes,Uri uri,CancellationToken token)=>RunAsync(()=>{repository!.RegisterReviewedCalendar(market,year,bytes,uri);return true;},token);
    private async Task<T> RunAsync<T>(Func<T> work,CancellationToken token)
    {
        // 单写入者门槛也用于查询，避免同一SQLite连接被界面命令并发使用。
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(()=>repository is {} current?current.ExecuteOperation(work,token):work(),token).ConfigureAwait(false); } finally {gate.Release();}
    }
    public Task<List<EtfSummary>> OverviewAsync(CancellationToken token)=>RunAsync(()=>repository!.Overview(),token);
    public Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>RunAsync(()=>repository!.Configuration(),token);
    public Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token)=>SaveConfigurationAsync(key,name,index,active,reason,expectedRevision,token,null);
    /// <summary>配置变更同样经过服务单写门槛和稳定命令去重。</summary>
    public Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token,Guid? commandId)=>RunAsync(()=>{repository!.SaveConfiguration(key,name,index,active,reason,expectedRevision,commandId);return true;},token);
    /// <summary>串行下载官方份额；按日期提交，取消后保留已完成日，重试使用原件哈希与事实幂等。</summary>
    public async Task<SyncReport> SyncRecentAsync(CancellationToken token)
    {
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        // 最新数据先采；历史失败不能把所有后续日期挡在旧游标后面。
        var recent=await SyncAsync(today.AddDays(-7),today,token,true);
        var gaps=await RunAsync(()=>repository!.DueHistoricalShareGaps(today.AddDays(-8),DateTimeOffset.UtcNow),token);
        if(gaps.Count==0)return recent;
        var from=gaps[0].Date;var to=from.AddDays(29)<today.AddDays(-8)?from.AddDays(29):today.AddDays(-8);
        var batch=gaps.Where(x=>x.Date>=from&&x.Date<=to).GroupBy(x=>x.Date).ToDictionary(x=>x.Key,x=>x.Select(g=>g.Key).ToHashSet());
        var backlog=await SyncAsync(from,to,token,false,batch);
        return new(recent.QueriedDays+backlog.QueriedDays,recent.SourceIssues+backlog.SourceIssues,recent.ReceivedRows+backlog.ReceivedRows,
            gaps.Any(x=>x.Date<from||x.Date>to));
    }
    public Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token)=>SyncAsync(from,to,token,false);
    /// <summary>自动补采逐日提交查询游标；来源故障日阻断游标前进，但保留其它已提交事实。</summary>
    private async Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token,bool trackProgress,IReadOnlyDictionary<DateOnly,HashSet<EtfKey>>? gapBatch=null)
    {
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        if(from>to || to>today || to.DayNumber-from.DayNumber>400)throw new InvalidDataException("采集区间必须不超过400个自然日，不能包含未来日期。");
        if(officialSource is null)throw new InvalidOperationException("官方采集适配器没有注册。");
        // 手动补历史与计划任务不重叠，避免同一来源突发请求。
        if(!await syncGate.WaitAsync(0,token))throw new InvalidDataException("已有联网采集运行中，请等待或取消当前操作。");
        var completed=0;var receivedRows=0;var issues=new HashSet<string>();var contiguous=true;int totalAdded=0,totalDuplicates=0,totalPending=0;
        try
        {
            await ReportAsync(new("RangeStarted",from));
            await officialSource.PrepareRangeAsync(from,to,token);
            for(var day=from;day<=to;day=day.AddDays(1))
            {
                token.ThrowIfCancellationRequested();NetworkStatus=$"两市正在获取 {day:yyyy-MM-dd}；已完成{completed}个查询日。";
                await ReportAsync(new("DayStarted",day));
                // 这里只读取监控配置，不为确定请求池重复计算全池指标和扫描全部原件。
                var activeKeys=await RunAsync(()=>repository!.Configuration().Where(x=>x.Active).Select(x=>new EtfKey(x.Market,x.Ticker)).ToList(),token);
                if(activeKeys.Count==0){NetworkStatus="当前没有启用监控的ETF，已停止本次采集；已提交事实保留。";return new(completed,issues.Count,receivedRows);}
                var poolKeys=await RunAsync(()=>activeKeys.Where(key=>repository!.IsReviewedTradingDay(key.Market,day)!=false
                    && (gapBatch is null||gapBatch.TryGetValue(day,out var due)&&due.Contains(key))).ToList(),token);
                if(poolKeys.Count==0)
                {
                    if(trackProgress&&contiguous)await RunAsync(()=>{repository!.RecordSyncCursor(day);return true;},token);
                    completed++;await ReportAsync(new("DayCommitted",day));continue;
                }
                var attempts=await RunAsync(()=>poolKeys.ToDictionary(key=>key,key=>repository!.BeginCollection(key,day)),token);
                SourceDay result;
                try
                {
                    await RunAsync(()=>{foreach(var id in attempts.Values)repository!.RecordCollection(id,"Fetching","OfficialSourceRequested");return true;},token);
                    result=await officialSource.FetchAsync(day,poolKeys,token).ConfigureAwait(false);
                }
                catch(Exception error)
                {
                    // 取消仍需落盘阶段；强杀则保留Fetching，重启后可识别未完成尝试。
                    await RunAsync(()=>{foreach(var id in attempts.Values)RecordCollectionFailure(id,error.Data["EtfSourceFailure"] as SourceFailure
                        ??new("",null,error is OperationCanceledException?"Interrupted":"SourceFailed",error is OperationCanceledException?"Cancelled":"SourceRequestFailed"));return true;},CancellationToken.None);
                    throw;
                }
                if(result.Issues?.Count>0||result.Failures?.Count>0)contiguous=false;
                var missing=await RunAsync(()=>poolKeys.Where(key=>repository!.IsReviewedTradingDay(key.Market,day)==true&&!result.Pages.Any(p=>p.Rows.Any(r=>r.Key==key&&r.Date==day))).ToArray(),token);
                if(missing.Length>0){contiguous=false;foreach(var key in missing)issues.Add($"{key.Market}:{key.Ticker}:{day:yyyy-MM-dd}:NotPublished");}
                receivedRows+=result.Pages.Sum(page=>page.Rows.Count(row=>poolKeys.Contains(row.Key)));
                foreach(var issue in result.Issues??[])issues.Add(issue);
                ShareImportOutcome outcome;
                try { outcome=await RunAsync(()=>
                {
                    int added=0,duplicates=0,pending=0;
                    var pool=repository!.Configuration().Where(x=>x.Active).Select(x=>new EtfKey(x.Market,x.Ticker)).ToHashSet();
                    foreach(var page in result.Pages)
                    {
                        token.ThrowIfCancellationRequested();var original=raw.Save(page.Content);
                        var rows=page.Rows.Where(x=>pool.Contains(x.Key)&&poolKeys.Contains(x.Key)&&x.Date==day).ToList();
                        var normalized=Encoding.UTF8.GetBytes("market,ticker,trade_date,total_shares,unit\n"+string.Join('\n',rows.Select(x=>$"{x.Key.Market},{x.Key.Ticker},{x.Date:yyyy-MM-dd},{x.Original.ToString(System.Globalization.CultureInfo.InvariantCulture)},万份")));
                        var fetch=repository.RecordFetch(raw.Save(normalized),page.Uri.AbsoluteUri,original,page.Market=="SZSE"?"OfficialSzseEtfJson":"OfficialSseEtfJson",page.Market=="SZSE"?"szse-etf-json-v1":"sse-etf-json-v1");
                        var pageKeys=poolKeys.Where(key=>key.Market==page.Market && (rows.Any(row=>row.Key==key)
                            || !result.Pages.Any(p=>p.Rows.Any(row=>row.Key==key)) && (page.Market=="SSE" || page.Uri.Query.Contains("txtDm="+key.Ticker,StringComparison.Ordinal)))).ToArray();
                        foreach(var key in pageKeys)
                        {
                            repository.RecordCollection(attempts[key],"RawSaved","OriginalAndNormalizedSaved",fetch);
                            repository.RecordCollection(attempts[key],"Parsed","OfficialResponseParsed",fetch);
                            repository.RecordCollection(attempts[key],"Validating","ShareQualityChecks",fetch);
                        }
                        try
                        {
                            // 官方日期可以保存份额事实，但不能据此补造完整交易日历或填零。
                            var imported=repository.ImportSharesDetailed(fetch,rows,token,requireCalendar:false);
                            added+=imported.Added;duplicates+=imported.Duplicates;pending+=imported.Pending;
                        }
                        catch{repository.MarkFailed(fetch);throw;}
                    }
                    var evidence=repository.Evidence();
                    var points=repository.PointsForKeys(poolKeys);
                    foreach(var key in poolKeys)
                    {
                        var failure=result.Failures?.FirstOrDefault(x=>x.Market==key.Market&&(x.Ticker is null||x.Ticker==key.Ticker));
                        if(failure is not null){RecordCollectionFailure(attempts[key],failure);continue;}
                        var attempt=repository.CollectionAttempts(key,day,day).First(x=>x.Id==attempts[key]);
                        var received=result.Pages.Any(p=>p.Rows.Any(r=>r.Key==key&&r.Date==day));
                        var revisions=evidence.Where(x=>x.Market==key.Market&&x.Ticker==key.Ticker&&x.TradeDate==day.ToString("yyyy-MM-dd")).ToArray();
                        var sourceFailed=result.Issues?.Any(issue=>issue.StartsWith(key.Market=="SSE"?"SSE":"SZSE "+key.Ticker,StringComparison.Ordinal))==true;
                        var state=!received?sourceFailed?"SourceFailed":repository.IsReviewedTradingDay(key.Market,day)==true?"NotPublished":"Missing"
                            :revisions.Any(x=>x.State=="NeedsReview")?"NeedsReview"
                            :points[key].Any(x=>x.Date==day)?"Accepted":"NeedsReview";
                        repository.RecordCollection(attempts[key],state,!received?sourceFailed?"SourceRequestFailed":"NoObservationReturned":state=="Accepted"?"AcceptedFactVerified":"QualityReviewRequired",attempt.FetchId);
                    }
                    if(trackProgress && contiguous)repository.RecordSyncCursor(day);
                    return new ShareImportOutcome(added,duplicates,pending);
                },token).ConfigureAwait(false); }
                catch(Exception error)
                {
                    await RunAsync(()=>{foreach(var pair in attempts)
                    {
                        var attempt=repository!.CollectionAttempts(pair.Key,day,day).First(x=>x.Id==pair.Value);
                        repository.RecordCollection(pair.Value,error is OperationCanceledException?"Interrupted":"ParseFailed",error is OperationCanceledException?"Cancelled":"ImportOrValidationFailed",attempt.FetchId);
                    }return true;},CancellationToken.None);
                    throw;
                }
                // 只有事实提交完成后才报告成功计数，取消或回滚不能产生已写入假象。
                totalAdded+=outcome.Added;totalDuplicates+=outcome.Duplicates;totalPending+=outcome.Pending;
                await ReportAsync(new("DayCommitted",day,Received:result.Pages.Sum(page=>page.Rows.Count(row=>poolKeys.Contains(row.Key))),Added:outcome.Added,Duplicates:outcome.Duplicates,Pending:outcome.Pending));
                completed++;
            }
            NetworkStatus=$"两市联网采集完成（{completed}个查询日）；{(issues.Count==0?"本次没有来源错误":string.Join("；",issues.Take(3)))}；完整交易日历与NAV金额仍待核验。";
            await ReportAsync(new("RangeCompleted",to,Received:receivedRows,Added:totalAdded,Duplicates:totalDuplicates,Pending:totalPending));
            return new(completed,issues.Count,receivedRows);
        }
        catch(OperationCanceledException){NetworkStatus=$"采集已取消，已完成{completed}日保留。";throw;}
        catch(Exception error){NetworkStatus=$"采集停止，已完成{completed}日保留；{error.Message}";throw;}
        finally{syncGate.Release();}
    }
    private void RecordCollectionFailure(string attempt,SourceFailure failure)
    {
        string? fetch=null;
        if(failure.OriginalHash is {} hash && failure.SourceUrl is {} url)
        {
            fetch=repository!.RecordFetch(hash,url,hash,failure.Market=="SZSE"?"OfficialSzseEtfJson":"OfficialSseEtfJson",failure.ParserVersion??"unknown");
            if(failure.State=="ParseFailed")repository.MarkFailed(fetch);
        }
        repository!.RecordCollection(attempt,failure.State,failure.Reason,fetch);
    }
    /// <summary>后台独占写路径：统一核验原件、协调质量问题与证据事件。</summary>
    public Task ReconcileEvidenceIntegrityAsync(CancellationToken token)=>RunAsync(()=>{repository!.ReconcileEvidenceIntegrity();return true;},token);
    public Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token)=>RunAsync(()=>repository!.Evidence(),token);
    public Task<List<EvidenceRow>> EvidenceAsync(EtfKey key,CancellationToken token)=>RunAsync(()=>repository!.Evidence(key),token);
    public Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token)=>RunAsync(()=>repository!.History(key),token);
    public Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token)=>RunAsync(()=>repository!.Points(key),token);
    /// <summary>在后台查询日历口径内的覆盖状态，不以份额记录本身推断交易日。</summary>
    public Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>RunAsync(()=>repository!.Coverage(key,from,to),token);
    public Task<List<CollectionAttempt>> CollectionAttemptsAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>RunAsync(()=>repository!.CollectionAttempts(key,from,to),token);
    public Task<List<EtfAcceptance>> AcceptanceAsync(DateOnly through,CancellationToken token)=>RunAsync(()=>repository!.Acceptance(through),token);
    public Task<List<EtfAcceptance>> AcceptanceAsync(EtfKey key,DateOnly through,CancellationToken token)=>RunAsync(()=>repository!.Acceptance(key,through),token);
    /// <summary>仅允许导出已登记修订的证据；目标文件安全和哈希验证由基础设施处理。</summary>
    public async Task ExportEvidenceAsync(string revisionId,bool original,string destination,CancellationToken token)
    {
        var row=await RunAsync(()=>repository!.Evidence().SingleOrDefault(x=>x.RevisionId==revisionId),token).ConfigureAwait(false)
            ??throw new InvalidDataException("所选证据修订不存在。");
        if(row.State=="Critical")throw new InvalidDataException("该修订的证据链损坏，拒绝导出为已核验文件。");
        var paths=context.DataPaths;
        // 数据库代次变化后仍保护整个插件目录，不能让导出覆盖旧库或恢复日志。
        var protectedDirectories=paths is IPluginWriteGuardedPaths guarded?guarded.ProtectedDirectories:[paths.Data,paths.Raw,paths.Log,paths.Backup];
        await raw.ExportVerifiedAsync(original?row.OriginalHash:row.Hash,destination,protectedDirectories,token).ConfigureAwait(false);
    }
    public async Task ImportAsync(string file,string originalFile,string url,bool calendar,CancellationToken token)
    {
        await context.Jobs.RunAsync("manual-import",async cancellation=>
        {
            var bytes=await raw.ReadImportFileAsync(file,cancellation).ConfigureAwait(false);
            var originalBytes=await raw.ReadImportFileAsync(originalFile,cancellation).ConfigureAwait(false);
            await RunAsync(()=>
            {
                var hash=raw.Save(bytes); var originalHash=raw.Save(originalBytes); var fetch=repository!.RecordFetch(hash,url,originalHash);
                try
                {
                    var text=new UTF8Encoding(false,true).GetString(bytes);
                    if(calendar) repository.ImportCalendar(fetch,ImportFormat.Calendar(text),cancellation);
                    else repository.ImportShares(fetch,ImportFormat.Shares(text),cancellation);
                    return true;
                }
                catch {repository.MarkFailed(fetch); throw;}
            },cancellation).ConfigureAwait(false);
        },token).ConfigureAwait(false);
    }
    public Task ReviewAsync(string id,bool accept,string reviewer,bool comparableConfirmed,CancellationToken token)=>ReviewAsync(id,accept,reviewer,comparableConfirmed,token,null);
    public Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token)=>MembershipAsync(key,name,index,active,reason,token,null);
    /// <summary>稳定命令标识随业务事务提交，防止调度记录提交前强杀导致重复审核或池变更。</summary>
    public Task ReviewAsync(string id,bool accept,string reviewer,bool comparableConfirmed,CancellationToken token,Guid? commandId)=>RunAsync(()=> {repository!.Review(id,accept,reviewer,comparableConfirmed,commandId);return true;},token);
    public Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token,Guid? commandId)=>RunAsync(()=> {repository!.SetMembership(key,name,index,active,reason,commandId);return true;},token);
    public async ValueTask DisposeAsync() {await gate.WaitAsync().ConfigureAwait(false);try {repository?.Dispose();repository=null;}finally {gate.Release();} }
}
