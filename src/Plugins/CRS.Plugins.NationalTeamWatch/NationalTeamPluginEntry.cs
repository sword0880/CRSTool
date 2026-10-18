using CRS.Plugin.Abstractions;
using CRS.Plugin.Wpf;
using CRS.Plugins.NationalTeamWatch.Presentation;
namespace CRS.Plugins.NationalTeamWatch;
/// <summary>桌面入口只建立管道展示会话；不创建市场库、下载器或桌面计划任务。</summary>
public sealed class NationalTeamPluginEntry:ICrsPluginEntry
{
    public IPluginSession CreateSession(IPluginHostContext context)=>new NationalTeamUiSession();
}
internal sealed class NationalTeamUiSession:IPluginSession,IPluginWpfSession
{
    private readonly CancellationTokenSource lifetime=new();
    private EtfViewModel? model;
    private EquityViewModel? equity;
    public async ValueTask StartAsync(CancellationToken token)
    {
        model=new(new WorkerEtfWorkspace(),lifetime.Token);
        equity=new(new WorkerEtfWorkspace(),lifetime.Token);
        // 未安装服务时页面仍可打开并说明连接状态，不启动本地替代采集器。
        await model.RefreshCommand.ExecuteAsync(null);
    }
    public IReadOnlyList<PluginPageContribution> GetPages()=>[new("etf-monitor","国家队观察 · ETF份额",()=>new EtfPage(model!)),new("direct-equity","国家队观察 · 直接持股",()=>new EquityPage(equity!))];
    public ValueTask StopAsync(CancellationToken token){lifetime.Cancel();return ValueTask.CompletedTask;}
    public ValueTask DisposeAsync(){lifetime.Cancel();lifetime.Dispose();model=null;equity=null;return ValueTask.CompletedTask;}
}
