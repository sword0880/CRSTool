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
    public NationalTeamSession(IPluginHostContext context)
    {
        // 只在入口绑定具体基础设施，应用用例依赖接口；数据库实际在后台初始化。
        var paths=context.DataPaths;
        if(paths is not IPluginWriteGuardedPaths guarded)throw new InvalidOperationException("宿主未提供升级写入门槛。");
        var raw=new RawEvidenceStore(paths.Raw,guarded.BeforeWrite);
        workspace=new(context,()=>new MarketRepository(paths.Data,raw,guarded.BeforeWrite),raw);
    }
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if(started) return; if(disposed) throw new ObjectDisposedException(nameof(NationalTeamSession));
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
        await workspace.InitializeAsync(linked.Token); model=new(workspace,lifetime.Token); await model.RefreshAsync(linked.Token); started=true;
    }
    public IReadOnlyList<PluginPageContribution> GetPages()=>started?[new("etf-monitor","国家队观察 · ETF份额",()=>new EtfPage(model!))]:[];
    public ValueTask StopAsync(CancellationToken cancellationToken) {lifetime.Cancel();model?.CancelCommand.Execute(null);started=false;return ValueTask.CompletedTask;}
    public async ValueTask DisposeAsync() {if(disposed)return;disposed=true;await StopAsync(CancellationToken.None);await workspace.DisposeAsync();model=null;lifetime.Dispose();}
}
