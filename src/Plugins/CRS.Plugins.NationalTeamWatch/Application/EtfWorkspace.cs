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
    public EtfWorkspace(IPluginHostContext context,Func<IMarketRepository> repositoryFactory,IRawEvidenceStore raw) {this.context=context;this.repositoryFactory=repositoryFactory;this.raw=raw;}
    public Task InitializeAsync(CancellationToken token)=>RunAsync(()=> { repository=repositoryFactory(); return true; },token);
    private async Task<T> RunAsync<T>(Func<T> work,CancellationToken token)
    {
        // 单写入者门槛也用于查询，避免同一SQLite连接被界面命令并发使用。
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(work,token).ConfigureAwait(false); } finally {gate.Release();}
    }
    public Task<List<EtfSummary>> OverviewAsync(CancellationToken token)=>RunAsync(()=>repository!.Overview(),token);
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
