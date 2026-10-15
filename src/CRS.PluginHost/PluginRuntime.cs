using System.IO;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugin.Wpf;
namespace CRS.PluginHost;
public enum PluginState { Discovered, Verified, Loaded, Started, Disabled, Stopping, Stopped, Failed }
/// <summary>界面只展示故障分类，完整异常仅供验证项目读取。</summary>
public sealed class PluginStatus(string id,string name)
{
    public string Id {get;}=id;
    public string Name {get;}=name;
    /// <summary>通过核验后选中的包版本；未通过清单核验时不伪造版本。</summary>
    public string? Version {get;internal set;}
    public PluginState State {get;internal set;}
    public string? Error {get;internal set;}
    public string? RecoveryMessage {get;internal set;}
    internal Exception? Failure {get;set;}
}
public sealed record HostedPage(string PluginId,PluginPageContribution Contribution)
{public string Route=>PluginId+":"+Contribution.RouteKey;}
/// <summary>官方插件默认上下文宿主，按日志选择成对的代码与数据库代次。</summary>
public sealed class PluginRuntime(string releaseRoot,string storageRoot,IReadOnlySet<string>? disabledAtStartup=null,Func<HostedPage,CancellationToken,Task>? validatePage=null):IAsyncDisposable
{
    private sealed record Running(IPluginSession Session,CancellationTokenSource Lifetime,PublicHttpGateway Http,PluginStatus Status,VerifiedPluginLoad Load,IDisposable Lease);
    private readonly List<Running> sessions=[];
    public List<PluginStatus> Statuses {get;}=[];
    public List<HostedPage> Pages {get;}=[];
    private readonly HashSet<string> disabled=ReadDisabled(storageRoot);
    private readonly HashSet<string> effectiveDisabled=disabledAtStartup is null?ReadDisabled(storageRoot):new(disabledAtStartup);
    private readonly CancellationTokenSource runtimeLifetime=new();
    // 桌面插件会话不再创建调度器；公开后台任务统一注册到 Worker。
    private Task? startup;
    private bool stopped,cancelled;
    public static IReadOnlySet<string> ReadStartupEnablement(string root)=>ReadDisabled(root);
    private static HashSet<string> ReadDisabled(string root)
    {
        var file=Path.Combine(root,"disabled.json");if(!File.Exists(file))return [];
        return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(file))??throw new InvalidDataException("插件启停配置损坏。");
    }
    public void SetEnabledForNextStart(string id,bool enabled)
    {
        // 配置落盘成功后再更新内存，失败时管理页重新打开也不能显示虚假的待生效状态。
        var updated=new HashSet<string>(disabled);
        if(enabled)updated.Remove(id);else updated.Add(id);
        Directory.CreateDirectory(storageRoot);var path=Path.Combine(storageRoot,"disabled.json");
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(updated));File.Move(path+".tmp",path,true);
        disabled.Clear();disabled.UnionWith(updated);
    }
    /// <summary>读取下次启动配置，界面不得把待生效设置当成本次会话状态。</summary>
    public bool IsEnabledForNextStart(string id)=>!disabled.Contains(id);
    public Task StartAsync(CancellationToken cancellationToken=default)
    {
        if(startup is not null || stopped)throw new InvalidOperationException("宿主会话不能重复启动。");
        return startup=StartCoreAsync(cancellationToken);
    }
    private async Task StartCoreAsync(CancellationToken external)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(external,runtimeLifetime.Token);var token=linked.Token;
        var index=Path.Combine(releaseRoot,"trusted-plugins.json");if(!File.Exists(index))return;
        var packages=JsonSerializer.Deserialize<TrustedPackage[]>(await File.ReadAllTextAsync(index,token),PackageVerifier.Json)??[];
        var verified=new List<(TrustedPackage Package,PluginManifest Manifest)>();
        foreach(var package in packages)
        {
            try {verified.Add((package,await Task.Run(()=>PackageVerifier.Verify(releaseRoot,package,new(1,0,0)),token)));}
            catch(Exception error) {Statuses.Add(new(package.Directory,package.Directory){State=PluginState.Failed,Error=error.GetType().Name,Failure=error});}
        }
        foreach(var group in verified.GroupBy(x=>x.Manifest.Id))
        {if(token.IsCancellationRequested)break;await StartPluginAsync(group.ToList(),token);}
    }
    private async Task StartPluginAsync(List<(TrustedPackage Package,PluginManifest Manifest)> versions,CancellationToken token)
    {
        var first=versions[0].Manifest;var status=new PluginStatus(first.Id,first.DisplayName){Version=versions.Count==1?first.Version:null};Statuses.Add(status);
        IDisposable? lease=null;VerifiedPluginLoad? load=null;IPluginSession? session=null;CancellationTokenSource? lifetime=null;PublicHttpGateway? http=null;
        var layout=new PluginStorageLayout(storageRoot,first.Id);var coordinator=layout.Coordinator;var candidateStarting=false;
        try
        {
            if(versions.Select(x=>x.Manifest.Version).Distinct().Count()!=versions.Count)throw new InvalidDataException("同一插件版本重复登记。");
            status.State=PluginState.Verified;if(effectiveDisabled.Contains(first.Id)){status.State=PluginState.Disabled;return;}
            lease=layout.AcquireLease();var journal=coordinator.Read();
            if(journal is {Stage:UpgradeStage.Prepared or UpgradeStage.Snapshotted or UpgradeStage.Migrated})
            {
                // 切换前旧库未被修改，只回退指针并保留快照与候选，不能覆盖原始数据。
                await coordinator.RecoverAsync(new NoVerification(),false,token);journal=coordinator.Read();
            }
            if(journal is {Stage:UpgradeStage.RequiresRecovery})throw new InvalidDataException("候选数据需要受控恢复。");
            var active=coordinator.Active();
            if(journal is {Stage:UpgradeStage.Switched} && active!=journal.Candidate)
            {
                await coordinator.RecoverAsync(new NoVerification(),false,token);journal=coordinator.Read();active=coordinator.Active();
                if(journal?.Stage==UpgradeStage.RequiresRecovery)throw new InvalidDataException("指针与新事实不一致，停止自动恢复。");
            }
            candidateStarting=journal?.Stage==UpgradeStage.Switched;
            var selected=active is null
                ?versions.Count==1?versions[0]:throw new InvalidDataException("多个预装版本必须有明确的当前版本指针。")
                :versions.SingleOrDefault(x=>x.Manifest.Version==active.PackageVersion);
            if(selected.Manifest is null)throw new InvalidDataException("当前版本不在可信发行清单中。");
            status.Version=selected.Manifest.Version;
            var generation=active?.DbGeneration??"legacy";
            if(active is not null && !File.Exists(layout.Database(generation)))throw new InvalidDataException("当前数据库代次缺失，拒绝创建空库替代。");
            // 在前一个插件加载后重新检查共享程序集，防止预检遗漏延迟出现的冲突。
            await Task.Run(()=>PackageVerifier.Verify(releaseRoot,selected.Package,new(1,0,0)),token);
            load=new(releaseRoot,selected.Package,selected.Manifest);status.State=PluginState.Loaded;
            lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);http=new();var paths=layout.Paths(selected.Manifest.Version,generation,lifetime.Token);
            PluginUpgradeService.MaintenanceParticipant? participant=null;
            if(candidateStarting)
            {
                if(load.Entry is not IPluginStorageMaintenanceEntry maintenanceEntry)throw new InvalidDataException("候选版本缺少存储验证契约。");
                participant=new(layout,maintenanceEntry.CreateStorageMaintenance(paths),selected.Manifest.SchemaVersion);
                await participant.VerifyCandidateAsync(active!,token);
            }
            session=load.Entry.CreateSession(new PluginContext(first.Id,new PluginJobs(lifetime.Token),paths,http,new Diagnostics((code,_)=>status.Error=code),selected.Manifest.RequestedCapabilities,new WorkerOwnedSchedules()));
            await session.StartAsync(lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(15),token);token.ThrowIfCancellationRequested();
            if(session is IPluginWpfSession wpf)
            {
                if(!selected.Manifest.RequestedCapabilities.Contains("navigation"))throw new InvalidDataException("未授予导航能力。");
                var pages=wpf.GetPages();var routes=new HashSet<string>();
                foreach(var page in pages)if(string.IsNullOrWhiteSpace(page.RouteKey) || page.RouteKey.Contains(':') || !routes.Add(page.RouteKey))throw new InvalidDataException("插件路由无效或重复。");
                Pages.AddRange(pages.Select(page=>new HostedPage(first.Id,page)));
                if(candidateStarting && pages.Count>0)
                {
                    if(validatePage is null)throw new InvalidDataException("WPF候选升级必须提供Dispatcher页面验证。");
                    foreach(var page in Pages.Where(x=>x.PluginId==first.Id))await validatePage(page,token);
                }
            }
            // 存储核验、会话启动和导航注册全部成功，才允许确认候选版本。
            if(candidateStarting && await coordinator.RecoverAsync(participant!,true,token)!=UpgradeStage.Committed)throw new InvalidDataException("候选启动确认失败。");
            sessions.Add(new(session,lifetime,http,status,load,lease));status.State=PluginState.Started;
            session=null;lifetime=null;http=null;load=null;lease=null;
        }
        catch(Exception error)
        {
            Pages.RemoveAll(x=>x.PluginId==first.Id);status.State=PluginState.Failed;status.Error=error.GetType().Name;status.Failure=error;lifetime?.Cancel();
            if(session is not null)
            {
                try {await session.StopAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));}
                catch(Exception) { /* 清理异常不覆盖原始启动原因。 */ }
            }
            if(candidateStarting)
            {
                try
                {
                    var result=await coordinator.RecoverAsync(new NoVerification(),false,CancellationToken.None);
                    status.RecoveryMessage=result==UpgradeStage.RolledBack?"候选启动失败，已回退版本指针；请重新启动应用。":"候选库已有新写入，已保留两份数据，需受控恢复。";
                }
                catch(Exception) {status.RecoveryMessage="升级日志无法恢复，请保留插件数据并检查恢复记录。";}
            }
        }
        finally {http?.Dispose();lifetime?.Dispose();load?.Dispose();lease?.Dispose();}
    }
    private sealed class NoVerification:IPluginUpgradeParticipant
    {
        // 只用于不确认候选的回退路径，不得伪造数据库核验成功。
        public Task SnapshotAsync(ActiveGeneration value,CancellationToken token)=>throw new NotSupportedException();
        public Task MigrateCandidateAsync(ActiveGeneration value,CancellationToken token)=>throw new NotSupportedException();
        public Task VerifyCandidateAsync(ActiveGeneration value,CancellationToken token)=>throw new NotSupportedException();
    }
    public void Cancel(){if(cancelled)return;cancelled=true;runtimeLifetime.Cancel();Pages.Clear();foreach(var item in sessions)item.Lifetime.Cancel();}
    public async ValueTask DisposeAsync()
    {
        if(stopped)return;stopped=true;Cancel();if(startup is not null){try{await startup;}catch(Exception){ /* 启动失败仍继续清理。 */ }}
        foreach(var item in sessions.AsEnumerable().Reverse())
        {
            item.Status.State=PluginState.Stopping;
            try
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await item.Session.StopAsync(timeout.Token).AsTask().WaitAsync(timeout.Token);
                await item.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));item.Status.State=PluginState.Stopped;
            }
            catch(Exception error){item.Status.State=PluginState.Failed;item.Status.Error=error.GetType().Name;}
            finally{item.Http.Dispose();item.Lifetime.Dispose();item.Load.Dispose();item.Lease.Dispose();}
        }
        sessions.Clear();runtimeLifetime.Dispose();
    }
    /// <summary>旧桌面调度契约明确拒绝，不能在后台服务不可用时回退为第二个调度器。</summary>
    private sealed class WorkerOwnedSchedules:IPluginScheduleGateway
    {
        public Task RegisterAsync(string id,string cron,string zone,Func<CancellationToken,Task> work,CancellationToken token,TimeSpan? delay=null,string? name=null)
            =>throw new NotSupportedException("公开计划任务由 Worker 唯一执行。");
    }
}
