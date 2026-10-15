using System.Text;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
/// <summary>插件应用用例：串行访问私有仓储，在后台执行导入、查询与证据核验。</summary>
public sealed class EtfWorkspace : IAsyncDisposable
{
    private readonly IPluginHostContext context;
    private readonly SemaphoreSlim gate=new(1);
    private readonly IRawEvidenceStore raw;
    private readonly Func<IMarketRepository> repositoryFactory;
    private IMarketRepository? repository;
    private IOfficialShareSource? officialSource;
    private readonly SemaphoreSlim syncGate=new(1);
    public string NetworkStatus {get;private set;}="等待自动采集；软件关闭或锁定后暂停。";
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
    /// <summary>串行下载官方份额；按日期提交，取消后保留已完成日，重试使用原件哈希与事实幂等。</summary>
    public Task<SyncReport> SyncRecentAsync(CancellationToken token)
    {
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        return SyncAsync(today.AddDays(-7),today,token);
    }
    public async Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token)
    {
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        if(from>to || to>today || to.DayNumber-from.DayNumber>400)throw new InvalidDataException("采集区间必须不超过400个自然日，不能包含未来日期。");
        if(officialSource is null)throw new InvalidOperationException("官方采集适配器没有注册。");
        // 手动补历史与计划任务不重叠，避免同一来源突发请求。
        if(!await syncGate.WaitAsync(0,token))throw new InvalidDataException("已有联网采集运行中，请等待或取消当前操作。");
        var completed=0;var issues=new HashSet<string>();
        try
        {
            for(var day=from;day<=to;day=day.AddDays(1))
            {
                token.ThrowIfCancellationRequested();NetworkStatus=$"两市正在获取 {day:yyyy-MM-dd}；已完成{completed}个查询日。";
                var poolKeys=await RunAsync(()=>repository!.Overview().Select(x=>new EtfKey(x.Market,x.Ticker)).ToList(),token);
                var result=await officialSource.FetchAsync(day,poolKeys,token).ConfigureAwait(false);
                foreach(var issue in result.Issues??[])issues.Add(issue);
                await RunAsync(()=>
                {
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
                            repository.ImportShares(fetch,rows,token,requireCalendar:false);
                        }
                        catch{repository.MarkFailed(fetch);throw;}
                    }
                    return true;
                },token).ConfigureAwait(false);
                completed++;
            }
            NetworkStatus=$"两市联网采集完成（{completed}个查询日）；{(issues.Count==0?"本次没有来源错误":string.Join("；",issues.Take(3)))}；完整交易日历与NAV金额仍待核验。";
            return new(completed,issues.Count);
        }
        catch(OperationCanceledException){NetworkStatus=$"采集已取消，已完成{completed}日保留。";throw;}
        catch(Exception error){NetworkStatus=$"采集停止，已完成{completed}日保留；{error.Message}";throw;}
        finally{syncGate.Release();}
    }
    public Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token)=>RunAsync(()=>repository!.Evidence(),token);
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
    public Task ReviewAsync(string id,bool accept,string reviewer,bool comparableConfirmed,CancellationToken token)=>RunAsync(()=> {repository!.Review(id,accept,reviewer,comparableConfirmed);return true;},token);
    public Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token)=>RunAsync(()=> {repository!.SetMembership(key,name,index,active,reason);return true;},token);
    public async ValueTask DisposeAsync() {await gate.WaitAsync().ConfigureAwait(false);try {repository?.Dispose();repository=null;}finally {gate.Release();} }
}
