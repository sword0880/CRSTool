using System.Text;
using CRS.Common.Concurrency;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>ETF采集、导入、证据与审核用例；通过共享会话访问ETF存储端口。</summary>
public sealed partial class EtfUseCases : MarketUseCaseScope
{
    private readonly IPluginHostContext context;
    private readonly IRawEvidenceStore raw;
    private readonly SemaphoreSlim syncGate=new(1,1);
    private IOfficialShareSource? officialSource;
    public string NetworkStatus {get;private set;}="等待后台服务采集；关闭或锁定桌面不影响任务运行。";
    internal EtfUseCases(MarketApplicationSession session,IPluginHostContext context,IRawEvidenceStore raw):base(session)
    {this.context=context;this.raw=raw;}
    public void SetOfficialSource(IOfficialShareSource source)=>officialSource=source;
    private Task<T> ReadAsync<T>(Func<IEtfRepository,T> query,CancellationToken token)=>ReadPortAsync(query,token);
    public Task RegisterCalendarAsync(string market,int year,byte[] bytes,Uri uri,CancellationToken token)=>RunAsync((IEtfRepository repository)=>{repository!.RegisterReviewedCalendar(market,year,bytes,uri);return true;},token);
    public Task<List<EtfSummary>> OverviewAsync(CancellationToken token)=>ReadAsync(current=>current.Overview(),token);
    public Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>ReadAsync(current=>current.Configuration(),token);
    public Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token)=>SaveConfigurationAsync(key,name,index,active,reason,expectedRevision,token,null);
    /// <summary>配置变更同样经过服务单写门槛和稳定命令去重。</summary>
    public Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token,Guid? commandId)=>RunAsync((IEtfRepository repository)=>{repository!.SaveConfiguration(key,name,index,active,reason,expectedRevision,commandId);return true;},token);
    /// <summary>串行下载官方份额；按日期提交，取消后保留已完成日，重试使用原件哈希与事实幂等。</summary>
    public async Task<SyncReport> SyncRecentAsync(CancellationToken token)
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
        // 最新数据先采；历史失败不能把所有后续日期挡在旧游标后面。
        var recent=await SyncAsync(today.AddDays(-7),today,token,true);
        var gaps=await ReadPortAsync((IShareCollectionRepository repository)=>repository.DueHistoricalShareGaps(today.AddDays(-8),CRS.Timing.BeijingClock.Now),token);
        if(gaps.Count==0)return recent;
        var from=gaps[0].Date;var to=from.AddDays(29)<today.AddDays(-8)?from.AddDays(29):today.AddDays(-8);
        var batch=gaps.Where(x=>x.Date>=from&&x.Date<=to).GroupBy(x=>x.Date).ToDictionary(x=>x.Key,x=>x.Select(g=>g.Key).ToHashSet());
        var backlog=await SyncAsync(from,to,token,false,batch);
        return new(recent.QueriedDays+backlog.QueriedDays,recent.SourceIssues+backlog.SourceIssues,recent.ReceivedRows+backlog.ReceivedRows,
            gaps.Any(x=>x.Date<from||x.Date>to))
        {ImportPreparationMilliseconds=recent.ImportPreparationMilliseconds+backlog.ImportPreparationMilliseconds,
            ImportCommitHeldMilliseconds=recent.ImportCommitHeldMilliseconds+backlog.ImportCommitHeldMilliseconds,
            ImportPreparationAttempts=recent.ImportPreparationAttempts+backlog.ImportPreparationAttempts};
    }
    public Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token)=>SyncAsync(from,to,token,false);
    /// <summary>自动补采逐日提交查询游标；来源故障日阻断游标前进，但保留其它已提交事实。</summary>
    private async Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token,bool trackProgress,IReadOnlyDictionary<DateOnly,HashSet<EtfKey>>? gapBatch=null)
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
        if(from>to || to>today || to.DayNumber-from.DayNumber>400)throw new InvalidDataException("采集区间必须不超过400个自然日，不能包含未来日期。");
        if(officialSource is null)throw new InvalidOperationException("官方采集适配器没有注册。");
        // 手动补历史与计划任务不重叠，避免同一来源突发请求。
        if(!await syncGate.WaitAsync(0,token))throw new InvalidDataException("已有联网采集运行中，请等待或取消当前操作。");
        var completed=0;var receivedRows=0;var issues=new HashSet<string>();var contiguous=true;int totalAdded=0,totalDuplicates=0,totalPending=0;
        long importPreparation=0,importHeld=0;var importAttempts=0;
        SyncReport Finish()=>new(completed,issues.Count,receivedRows){ImportPreparationMilliseconds=importPreparation,ImportCommitHeldMilliseconds=importHeld,ImportPreparationAttempts=importAttempts};
        try
        {
            await ReportAsync(new("RangeStarted",from));
            await officialSource.PrepareRangeAsync(from,to,token);
            for(var day=from;day<=to;day=day.AddDays(1))
            {
                token.ThrowIfCancellationRequested();NetworkStatus=$"两市正在获取 {day:yyyy-MM-dd}；已完成{completed}个查询日。";
                await ReportAsync(new("DayStarted",day));
                // 这里只读取监控配置，不为确定请求池重复计算全池指标和扫描全部原件。
                var activeKeys=await ReadPortAsync((IEtfReadRepository repository)=>repository.Configuration().Where(x=>x.Active).Select(x=>new EtfKey(x.Market,x.Ticker)).ToList(),token);
                if(activeKeys.Count==0){NetworkStatus="当前没有启用监控的ETF，已停止本次采集；已提交事实保留。";return Finish();}
                var poolKeys=await ReadPortAsync((IShareCollectionRepository repository)=>activeKeys.Where(key=>repository.IsReviewedTradingDay(key.Market,day)!=false
                    && (gapBatch is null||gapBatch.TryGetValue(day,out var due)&&due.Contains(key))).ToList(),token);
                if(poolKeys.Count==0)
                {
                    if(trackProgress&&contiguous)await RunAsync((IEtfRepository repository)=>{repository!.RecordSyncCursor(day);return true;},token);
                    completed++;await ReportAsync(new("DayCommitted",day));continue;
                }
                var attempts=await RunAsync((IEtfRepository repository)=>poolKeys.ToDictionary(key=>key,key=>repository!.BeginCollection(key,day)),token);
                SourceDay result;
                try
                {
                    await RunAsync((IEtfRepository repository)=>{foreach(var id in attempts.Values)repository!.RecordCollection(id,"Fetching","OfficialSourceRequested");return true;},token);
                    result=await officialSource.FetchAsync(day,poolKeys,token).ConfigureAwait(false);
                }
                catch(Exception error)
                {
                    // 取消仍需落盘阶段；强杀则保留Fetching，重启后可识别未完成尝试。
                    await RunAsync((IEtfRepository repository)=>{foreach(var id in attempts.Values)RecordCollectionFailure(repository,id,error.Data["EtfSourceFailure"] as SourceFailure
                        ??new("",null,error is OperationCanceledException?"Interrupted":"SourceFailed",error is OperationCanceledException?"Cancelled":"SourceRequestFailed"));return true;},CancellationToken.None);
                    throw;
                }
                if(result.Issues?.Count>0||result.Failures?.Count>0)contiguous=false;
                CollectionCommitResult committed;
                try
                {
                var missing=await ReadPortAsync((IShareCollectionRepository repository)=>poolKeys.Where(key=>repository.IsReviewedTradingDay(key.Market,day)==true&&!result.Pages.Any(p=>p.Rows.Any(r=>r.Key==key&&r.Date==day))).ToArray(),token);
                if(missing.Length>0){contiguous=false;foreach(var key in missing)issues.Add($"{key.Market}:{key.Ticker}:{day:yyyy-MM-dd}:NotPublished");}
                receivedRows+=result.Pages.Sum(page=>page.Rows.Count(row=>poolKeys.Contains(row.Key)));
                foreach(var issue in result.Issues??[])issues.Add(issue);
                committed=await CommitCollectedDayAsync(result.Pages,poolKeys,day,(repository,preparedPages)=>
                {
                    int added=0,duplicates=0,pending=0;
                    foreach(var prepared in preparedPages)
                    {
                        token.ThrowIfCancellationRequested();var page=prepared.Source;var rows=prepared.Rows;
                        var fetch=repository.RecordFetch(prepared.NormalizedHash,page.Uri.AbsoluteUri,prepared.OriginalHash,page.Market=="SZSE"?"OfficialSzseEtfJson":"OfficialSseEtfJson",page.Market=="SZSE"?"szse-etf-json-v1":"sse-etf-json-v1");
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
                    // 批事实仍核验原有仪表盘依赖；待审修订会在事实SQL中阻断该日，勿再次物化整库证据行。
                    var points=repository.PointsForKeys(poolKeys);
                    foreach(var key in poolKeys)
                    {
                        var failure=result.Failures?.FirstOrDefault(x=>x.Market==key.Market&&(x.Ticker is null||x.Ticker==key.Ticker));
                        if(failure is not null){RecordCollectionFailure(repository,attempts[key],failure);continue;}
                        var attempt=repository.CollectionAttempts(key,day,day).First(x=>x.Id==attempts[key]);
                        var received=result.Pages.Any(p=>p.Rows.Any(r=>r.Key==key&&r.Date==day));
                        var sourceFailed=result.Issues?.Any(issue=>issue.StartsWith(key.Market=="SSE"?"SSE":"SZSE "+key.Ticker,StringComparison.Ordinal))==true;
                        var state=!received?sourceFailed?"SourceFailed":repository.IsReviewedTradingDay(key.Market,day)==true?"NotPublished":"Missing"
                            :points[key].Any(x=>x.Date==day)?"Accepted":"NeedsReview";
                        repository.RecordCollection(attempts[key],state,!received?sourceFailed?"SourceRequestFailed":"NoObservationReturned":state=="Accepted"?"AcceptedFactVerified":"QualityReviewRequired",attempt.FetchId);
                    }
                    if(trackProgress && contiguous)repository.RecordSyncCursor(day);
                    return new ShareImportOutcome(added,duplicates,pending);
                },token).ConfigureAwait(false); }
                catch(Exception error)
                {
                    await RunAsync((IEtfRepository repository)=>{foreach(var pair in attempts)
                    {
                        var attempt=repository!.CollectionAttempts(pair.Key,day,day).First(x=>x.Id==pair.Value);
                        var deferred=error is WorkAdmissionException;
                        repository.RecordCollection(pair.Value,error is OperationCanceledException||deferred?"Interrupted":"ParseFailed",
                            error is OperationCanceledException?"Cancelled":error is WorkAdmissionException{Reason:"InputsChanged"}?"InputsChanged":deferred?"AdmissionDeferred":"ImportOrValidationFailed",attempt.FetchId);
                    }return true;},CancellationToken.None);
                    throw;
                }
                var outcome=committed.Outcome;
                importPreparation+=committed.PreparationMilliseconds;importHeld+=committed.CommitHeldMilliseconds;importAttempts+=committed.Attempts;
                // 只有事实提交完成后才报告成功计数，取消或回滚不能产生已写入假象。
                totalAdded+=outcome.Added;totalDuplicates+=outcome.Duplicates;totalPending+=outcome.Pending;
                await ReportAsync(new("DayCommitted",day,Received:result.Pages.Sum(page=>page.Rows.Count(row=>poolKeys.Contains(row.Key))),Added:outcome.Added,Duplicates:outcome.Duplicates,Pending:outcome.Pending));
                completed++;
            }
            NetworkStatus=$"两市联网采集完成（{completed}个查询日）；{(issues.Count==0?"本次没有来源错误":string.Join("；",issues.Take(3)))}；完整交易日历与NAV金额仍待核验。";
            await ReportAsync(new("RangeCompleted",to,Received:receivedRows,Added:totalAdded,Duplicates:totalDuplicates,Pending:totalPending));
            return Finish();
        }
        catch(OperationCanceledException){NetworkStatus=$"采集已取消，已完成{completed}日保留。";throw;}
        catch(Exception error){NetworkStatus=$"采集停止，已完成{completed}日保留；{error.Message}";throw;}
        finally{syncGate.Release();}
    }
    private void RecordCollectionFailure(IEtfRepository repository,string attempt,SourceFailure failure)
    {
        string? fetch=null;
        if(failure.OriginalHash is {} hash && failure.SourceUrl is {} url)
        {
            fetch=repository!.RecordFetch(hash,url,hash,failure.Market=="SZSE"?"OfficialSzseEtfJson":"OfficialSseEtfJson",failure.ParserVersion??"unknown");
            if(failure.State=="ParseFailed")repository.MarkFailed(fetch);
        }
        repository!.RecordCollection(attempt,failure.State,failure.Reason,fetch);
    }
    public Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token)=>ReadAsync(current=>current.Evidence(),token);
    public Task<List<EvidenceRow>> EvidenceAsync(EtfKey key,CancellationToken token)=>ReadAsync(current=>current.Evidence(key),token);
    public Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token)=>ReadAsync(current=>current.History(key),token);
    public Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token)=>ReadAsync(current=>current.Points(key),token);
    /// <summary>在后台查询日历口径内的覆盖状态，不以份额记录本身推断交易日。</summary>
    public Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>ReadAsync(current=>current.Coverage(key,from,to),token);
    public Task<List<CollectionAttempt>> CollectionAttemptsAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>ReadAsync(current=>current.CollectionAttempts(key,from,to),token);
    public Task<List<EtfAcceptance>> AcceptanceAsync(DateOnly through,CancellationToken token)=>ReadAsync(current=>current.Acceptance(through),token);
    public Task<List<EtfAcceptance>> AcceptanceAsync(EtfKey key,DateOnly through,CancellationToken token)=>ReadAsync(current=>current.Acceptance(key,through),token);
    /// <summary>仅允许导出已登记修订的证据；目标文件安全和哈希验证由基础设施处理。</summary>
    public async Task ExportEvidenceAsync(string revisionId,bool original,string destination,CancellationToken token)
    {
        var row=await RunAsync((IEtfRepository repository)=>repository!.Evidence().SingleOrDefault(x=>x.RevisionId==revisionId),token).ConfigureAwait(false)
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
            await RunAsync((IEtfRepository repository)=>
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
    public Task ReviewAsync(string id,bool accept,string reviewer,bool comparableConfirmed,CancellationToken token,Guid? commandId)=>RunAsync((IEtfRepository repository)=> {repository!.Review(id,accept,reviewer,comparableConfirmed,commandId);return true;},token);
    public Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token,Guid? commandId)=>RunAsync((IEtfRepository repository)=> {repository!.SetMembership(key,name,index,active,reason,commandId);return true;},token);
}
