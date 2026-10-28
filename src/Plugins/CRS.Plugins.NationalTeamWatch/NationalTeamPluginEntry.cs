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
    private NavStudyViewModel? nav;
    private EtfHoldingsViewModel? holdings;
    private MarketCycleStudyViewModel? cycle;
    private JointExitStudyViewModel? joint;
    public ValueTask StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var etfWorkspace=new WorkerEtfWorkspace();
        model=new(etfWorkspace,etfWorkspace,etfWorkspace,lifetime.Token);
        equity=new(new WorkerEtfWorkspace(),lifetime.Token);
        industry=new(new WorkerEtfWorkspace(),lifetime.Token);
        indices=new(new WorkerEtfWorkspace(),lifetime.Token);
        cycle=new(new WorkerEtfWorkspace(),lifetime.Token);
        var jointWorkspace=new WorkerEtfWorkspace();joint=new(jointWorkspace,jointWorkspace,lifetime.Token);
        var navWorkspace=new WorkerEtfWorkspace();nav=new(navWorkspace,navWorkspace,lifetime.Token);
        var holdingsWorkspace=new WorkerEtfWorkspace();holdings=new(holdingsWorkspace,holdingsWorkspace,holdingsWorkspace,lifetime.Token);
        // 启动只注册页面；数据读取在页面打开后执行，不能占用宿主的启动期限。
        return ValueTask.CompletedTask;
    }
    public IReadOnlyList<PluginPageContribution> GetPages()=>[
        // 唯一主菜单入口；模块在工作台内按证据链分类，页面仅在选中时创建。
        new("national-team-workspace","国家队研究工作台",()=>new NationalTeamWorkspacePage(
            NationalTeamWorkspaceCatalog.Create(model!,equity!,industry!,indices!,nav!,holdings!,cycle!,joint!)))
    ];
    public ValueTask StopAsync(CancellationToken token){lifetime.Cancel();return ValueTask.CompletedTask;}
    public ValueTask DisposeAsync(){lifetime.Cancel();holdings?.Cancel();lifetime.Dispose();model=null;equity=null;industry=null;indices=null;nav=null;holdings=null;cycle=null;joint=null;return ValueTask.CompletedTask;}
}
