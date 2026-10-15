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
    private async Task<T> RunAsync<T>(Func<T> work,CancellationToken token)
    {
        // 单写入者门槛也用于查询，避免同一SQLite连接被界面命令并发使用。
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(work,token).ConfigureAwait(false); } finally {gate.Release();}
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
        // 游标表示已查询日，不代表完整交易日历；重叠七天允许来源迟发修订被再次发现。
        var cursor=await RunAsync(()=>repository!.ReadSyncCursor(),token);
        var from=(cursor??today).AddDays(-7);var to=from.AddDays(29)<today?from.AddDays(29):today;
        var report=await SyncAsync(from,to,token,true);
        return report with{HasMore=to<today};
    }
    public Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token)=>SyncAsync(from,to,token,false);
    /// <summary>自动补采逐日提交查询游标；来源故障日阻断游标前进，但保留其它已提交事实。</summary>
    private async Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token,bool trackProgress)
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
            for(var day=from;day<=to;day=day.AddDays(1))
            {
                token.ThrowIfCancellationRequested();NetworkStatus=$"两市正在获取 {day:yyyy-MM-dd}；已完成{completed}个查询日。";
                await ReportAsync(new("DayStarted",day));
                var poolKeys=await RunAsync(()=>repository!.Overview().Select(x=>new EtfKey(x.Market,x.Ticker)).ToList(),token);
                if(poolKeys.Count==0){NetworkStatus="当前没有启用监控的ETF，已停止本次采集；已提交事实保留。";return new(completed,issues.Count,receivedRows);}
                var result=await officialSource.FetchAsync(day,poolKeys,token).ConfigureAwait(false);
                if(result.Issues?.Count>0)contiguous=false;
                receivedRows+=result.Pages.Sum(page=>page.Rows.Count(row=>poolKeys.Contains(row.Key)));
                foreach(var issue in result.Issues??[])issues.Add(issue);
                var outcome=await RunAsync(()=>
                {
                    int added=0,duplicates=0,pending=0;
                    var pool=repository!.Overview().Select(x=>new EtfKey(x.Market,x.Ticker)).ToHashSet();
                    foreach(var page in result.Pages)
                    {
                        token.ThrowIfCancellationRequested();var original=raw.Save(page.Content);
                        var rows=page.Rows.Where(x=>pool.Contains(x.Key)).ToList();
                        var normalized=Encoding.UTF8.GetBytes("market,ticker,trade_date,total_shares,unit\n"+string.Join('\n',rows.Select(x=>$"{x.Key.Market},{x.Key.Ticker},{x.Date:yyyy-MM-dd},{x.Original.ToString(System.Globalization.CultureInfo.InvariantCulture)},万份")));
                        var fetch=repository.RecordFetch(raw.Save(normalized),page.Uri.AbsoluteUri,original,page.Market=="SZSE"?"OfficialSzseEtfJson":"OfficialSseEtfJson",page.Market=="SZSE"?"szse-etf-json-v1":"sse-etf-json-v1");
                        try
                        {
                            // 官方日期可以保存份额事实，但不能据此补造完整交易日历或填零。
                            var imported=repository.ImportSharesDetailed(fetch,rows,token,requireCalendar:false);
                            added+=imported.Added;duplicates+=imported.Duplicates;pending+=imported.Pending;
                        }
                        catch{repository.MarkFailed(fetch);throw;}
                    }
                    if(trackProgress && contiguous)repository.RecordSyncCursor(day);
                    return new ShareImportOutcome(added,duplicates,pending);
                },token).ConfigureAwait(false);
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
    public Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token)=>RunAsync(()=>repository!.Evidence(),token);
    public Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token)=>RunAsync(()=>repository!.History(key),token);
    public Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token)=>RunAsync(()=>repository!.Points(key),token);
    /// <summary>在后台查询日历口径内的覆盖状态，不以份额记录本身推断交易日。</summary>
    public Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>RunAsync(()=>repository!.Coverage(key,from,to),token);
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
