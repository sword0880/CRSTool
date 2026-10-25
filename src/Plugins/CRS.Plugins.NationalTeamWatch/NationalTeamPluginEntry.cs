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
    private IndustryResearchViewModel? industry;
    private IndexResearchViewModel? indices;
    public ValueTask StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        model=new(new WorkerEtfWorkspace(),lifetime.Token);
        equity=new(new WorkerEtfWorkspace(),lifetime.Token);
        industry=new(new WorkerEtfWorkspace(),lifetime.Token);
        indices=new(new WorkerEtfWorkspace(),lifetime.Token);
        // 启动只注册页面；数据读取在页面打开后执行，不能占用宿主的启动期限。
        return ValueTask.CompletedTask;
    }
    public IReadOnlyList<PluginPageContribution> GetPages()=>[new("etf-monitor","国家队观察 · ETF份额",()=>new EtfPage(model!)),new("direct-equity","国家队观察 · 直接持股",()=>new EquityPage(equity!)),new("industry-research","行业估值研究",()=>new IndustryResearchHostPage(industry!,indices!))];
    public ValueTask StopAsync(CancellationToken token){lifetime.Cancel();return ValueTask.CompletedTask;}
    public ValueTask DisposeAsync(){lifetime.Cancel();lifetime.Dispose();model=null;equity=null;industry=null;indices=null;return ValueTask.CompletedTask;}
}
