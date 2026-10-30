using CRS.Plugin.Abstractions;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>后台用例组合根；界面工作区合同仅由Worker客户端实现。</summary>
public sealed class NationalTeamApplication : IAsyncDisposable
{
    private readonly MarketApplicationSession session;
    public EtfUseCases Etf {get;}
    public EtfHoldingsUseCases Holdings {get;}
    public EquityUseCases Equity {get;}
    public ExitAlertUseCases Alerts {get;}
    public IndustryPeUseCases Industry {get;}
    public IndexSeriesUseCases Index {get;}
    public MarketActivityUseCases Activity {get;}
    public NavUseCases Nav {get;}
    public EvidenceUseCases Evidence {get;}
    public MarketCycleStudyUseCases Cycle {get;}
    public NationalTeamApplication(IPluginHostContext context,Func<IMarketRepository> factory,IRawEvidenceStore raw,IEtfHolderCandidateParser holderParser)
    {
        if(raw is not IRawEvidenceVerifier||raw is not IRawEvidenceStability)throw new NotSupportedException("后台原件存储缺少必需的完整核验或稳定租约能力。");
        session=new(factory);Etf=new(session,context,raw);Equity=new(session);Alerts=new(session,raw);
        Industry=new(session);Index=new(session);Activity=new(session);Nav=new(session);Evidence=new(session,raw);Holdings=new(session,holderParser);
        Cycle=new(session);
    }
    public void SetProgressSink(Func<EtfProgress,Task>? sink)=>session.Progress=sink;
    public Task InitializeAsync(CancellationToken token)=>session.InitializeAsync(token);
    public ValueTask DisposeAsync()=>session.DisposeAsync();
}
