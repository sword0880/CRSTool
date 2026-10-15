using CRS.Plugin.Abstractions;
using CRS.Plugin.Wpf;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Presentation;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
namespace CRS.Plugins.NationalTeamWatch;
/// <summary>插件私有组合根，生命周期和资源均由插件会话持有。</summary>
public sealed class NationalTeamPluginEntry : ICrsPluginEntry,IPluginStorageMaintenanceEntry
{
    public IPluginSession CreateSession(IPluginHostContext context)=>new NationalTeamSession(context);
    public IPluginStorageMaintenance CreateStorageMaintenance(IPluginDataPaths paths)=>new MarketStorageMaintenance(paths);
}
internal sealed class NationalTeamSession : IPluginSession,IPluginWpfSession
{
    private readonly EtfWorkspace workspace;
    private readonly CancellationTokenSource lifetime=new();
    private EtfViewModel? model;
    private bool started,disposed;
    private readonly IPluginHostContext context;
    private System.Windows.Threading.Dispatcher? dispatcher;
    public NationalTeamSession(IPluginHostContext context)
    {
        this.context=context;
        // 只在入口绑定具体基础设施，应用用例依赖接口；数据库实际在后台初始化。
        var paths=context.DataPaths;
        if(paths is not IPluginWriteGuardedPaths guarded)throw new InvalidOperationException("宿主未提供升级写入门槛。");
        var raw=new RawEvidenceStore(paths.Raw,guarded.BeforeWrite);
        workspace=new(context,()=>new MarketRepository(paths.Data,raw,guarded.BeforeWrite),raw);
        workspace.SetOfficialSource(new OfficialShareSources(context.Http,raw));
    }
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if(started) return; if(disposed) throw new ObjectDisposedException(nameof(NationalTeamSession));
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
        await workspace.InitializeAsync(linked.Token); model=new(workspace,lifetime.Token); await model.RefreshAsync(linked.Token); started=true;
        dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
        if(context is IPluginScheduleContext scheduled)
            await scheduled.Schedules.RegisterAsync("official-share-sync","0 15 23 * * ?","China Standard Time",SyncWorkAsync,linked.Token,TimeSpan.FromSeconds(60),"官方ETF份额同步");
    }
    private async Task SyncWorkAsync(CancellationToken token)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);token=linked.Token;
        try
        {
            // 调度工作只调用后台用例，不经由会吞掉错误的界面命令。
            var report=await workspace.SyncRecentAsync(token).ConfigureAwait(false);
            if(report.SourceIssues>0)throw new PluginTaskIncompleteException();
        }
        finally
        {
            if(!token.IsCancellationRequested && dispatcher is not null && model is not null && !dispatcher.HasShutdownStarted)
            {
                try{await dispatcher.InvokeAsync(()=>model.RefreshFromScheduleAsync(token)).Task.Unwrap().ConfigureAwait(false);}
                catch(OperationCanceledException){ /* 关闭窗口不改变已经保存的来源执行结果。 */ }
                catch(Exception){context.Diagnostics.Report("SYNC_UI_REFRESH_FAILED","采集后界面刷新失败。");}
            }
        }
    }
    public IReadOnlyList<PluginPageContribution> GetPages()=>started?[new("etf-monitor","国家队观察 · ETF份额",()=>new EtfPage(model!))]:[];
    public ValueTask StopAsync(CancellationToken cancellationToken) {lifetime.Cancel();model?.CancelCommand.Execute(null);started=false;return ValueTask.CompletedTask;}
    public async ValueTask DisposeAsync() {if(disposed)return;disposed=true;await StopAsync(CancellationToken.None);await workspace.DisposeAsync();model=null;lifetime.Dispose();}
}
