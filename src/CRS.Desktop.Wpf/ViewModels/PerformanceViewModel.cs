using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application;
using CRS.Application.Performance;
using CRS.Domain.Performance;
using CRS.DesktopClient.Charts;

namespace CRS.DesktopClient.ViewModels;

public sealed partial class PerformanceAccountSelection(InvestmentAccount account,Action changed,string? displayAlias=null) : ObservableObject
{
    public InvestmentAccount Account {get;}=account;
    public string Alias=>displayAlias??Account.Alias;
    public string Broker=>Account.Broker;
    public string BaseCurrency=>Account.BaseCurrency;
    [ObservableProperty] private bool selected=account.Included;
    partial void OnSelectedChanged(bool value)=>changed();
}
public sealed record PerformanceMetricDisplay(string Name,string Value,string Quality,string Explanation);
public sealed record PerformanceEventDisplay(string Id,string AccountAlias,string Date,string Type,decimal Amount,string Currency,string Source,string Row,string Security)
{public string Label=>$"{AccountAlias} · {Date} · {Type} · {Amount:N2} {Currency}";}
public sealed record PerformanceEventOption(InvestmentEventKind Kind,string Name);
/// <summary>绩效拥有独立结果，税务页面变化不清空/改写绩效；锁定时共同清理。</summary>
public partial class PerformanceViewModel : ObservableObject
{
    private readonly WorkspaceState state;
    private readonly IPerformanceUseCases? useCases;
    private CancellationTokenSource? operation;
    private PerformanceWorkspace? workspace;
    private PerformanceRun? run;
    private bool closed;
    private bool rebuildingWorkspace;
    private long parameterRevision;
    private string inputSnapshotId="";
    private PerformanceAccountPrivacy privacy=new([],[]);
    [ObservableProperty] private string accountDetails="";
    public bool HasAccountDetails=>AccountDetails.Length>0;
    partial void OnAccountDetailsChanged(string value)=>OnPropertyChanged(nameof(HasAccountDetails));
    [RelayCommand] private void HideAccountDetails()=>AccountDetails="";
    [RelayCommand] private void RevealAccount(PerformanceAccountSelection? account)
    {if(!closed && account is not null && Accounts.Contains(account))AccountDetails=privacy.Details(account.Account);}
    public bool OnlineUpdatesAllowed=>!closed && useCases?.OnlineUpdatesAllowed==true;
    public bool IsOffline=>!OnlineUpdatesAllowed;
    public string NetworkStatus=>OnlineUpdatesAllowed?"已授权：本次解锁可按需更新指数及汇率。":"仅离线：计算和对比只使用已保存资料。";
    private bool CanDownloadBenchmark()=>CanCalculate() && OnlineUpdatesAllowed;
    [RelayCommand(CanExecute=nameof(CanRun))] private void AuthorizeOnlineUpdates()
    {
        if(!CanRun() || OnlineUpdatesAllowed)return;
        var authorized=state.Interaction.ConfirmPerformanceOnlineUpdates();
        if(!closed && useCases?.SetOnlineUpdatesAllowed(authorized)!=true && authorized)state.Status="当前会话未提供在线行情能力，保持仅离线。";
        NotifyNetworkMode();
    }
    [RelayCommand] private void UseOffline()
    {useCases?.SetOnlineUpdatesAllowed(false);NotifyNetworkMode();}
    private void NotifyNetworkMode()
    {OnPropertyChanged(nameof(OnlineUpdatesAllowed));OnPropertyChanged(nameof(IsOffline));OnPropertyChanged(nameof(NetworkStatus));DownloadBenchmarkCommand.NotifyCanExecuteChanged();}
    [ObservableProperty] private bool hasNetAssetsPoints;
    [ObservableProperty] private bool hasComparisonPoints;
    public bool NoNetAssetsPoints=>!HasNetAssetsPoints;
    public bool NoComparisonPoints=>!HasComparisonPoints;
    partial void OnHasNetAssetsPointsChanged(bool value)=>OnPropertyChanged(nameof(NoNetAssetsPoints));
    partial void OnHasComparisonPointsChanged(bool value)=>OnPropertyChanged(nameof(NoComparisonPoints));
    [ObservableProperty] private string netAssetsStatus="尚未计算，请选择账户和期间后点击“更新曲线”。";
    [ObservableProperty] private string comparisonStatus="尚未计算，请点击“更新曲线”。";
    [ObservableProperty] private ChartPresentation netAssetsChart=ChartPresentation.Empty;
    [ObservableProperty] private ChartPresentation comparisonChart=ChartPresentation.Empty;
    [ObservableProperty] private string curveExplanation="默认仅离线，使用已保存资料；如需获取指数行情，请选择允许在线更新并授权。缺少资料时保留空白。";
    public string[] Benchmarks {get;}=["SPX","HSI","IXIC"];
    [ObservableProperty] private string benchmark="SPX";
    partial void OnBenchmarkChanged(string value) {ComparisonChart=ChartPresentation.Empty;HasComparisonPoints=false;ComparisonStatus="指数已切换，请点击“更新曲线”；缺少报价时可点击“联网更新并重算”。";CurveExplanation=ComparisonStatus;}
    private bool CanRefreshCurve()=>CanExport() || CanCalculate();
    [RelayCommand(CanExecute=nameof(CanRefreshCurve))]
    private async Task RefreshCurveAsync()
    {if(run is null)await CalculateAsync();else await Run(UpdateCurve);}
    private async Task UpdateCurve(CancellationToken token)
    {
        var expectedRun=run;var expectedBenchmark=Benchmark;var revision=parameterRevision;
        if(expectedRun is null)return;
        var curve=await useCases!.CurveAsync(expectedRun.Id,expectedBenchmark,token);
        token.ThrowIfCancellationRequested();
        if(closed || run!=expectedRun || parameterRevision!=revision || Benchmark!=expectedBenchmark)return;
        NetAssetsChart=DesktopChartAdapter.NetAssets(curve,run.Result.Request.Currency);ComparisonChart=DesktopChartAdapter.Comparison(curve);
        var navCount=curve.Points.Count(p=>p.NetAssets.HasValue);var unitCount=curve.Points.Count(p=>p.UnitValue.HasValue);var indexCount=curve.Points.Count(p=>p.BenchmarkValue.HasValue);
        HasNetAssetsPoints=navCount>0;HasComparisonPoints=unitCount>0 || indexCount>0;
        var reasons=run.Result.Issues.Where(i=>i.Code is "FX_MISSING" or "INITIAL_NAV_MISSING" || i.Code.Contains("VALUATION",StringComparison.Ordinal)).Select(i=>i.Explanation).Distinct().Take(2);
        NetAssetsStatus=navCount==0?"没有可计算的净资产点。请检查“资金流与异常检查”中的净值及估值日汇率问题。 "+string.Join(" ",reasons)
            :navCount==1?"目前只有 1 个有效净值点，只能显示单点；至少 2 个不同日期的完整估值才能画折线。":$"已显示 {navCount} 个净资产点。";
        ComparisonStatus=$"组合净值 {unitCount} 个有效点，指数 {indexCount} 个有效点。"+(unitCount<2?" 组合 TWR 需要完整期初、期末净值及资金流证据。":"")+(indexCount<2?" 指数需要所选期间的报价和对比用汇率；可点击“联网更新并重算”。":"");
        CurveExplanation=curve.Explanation+(curve.Points.Any(p=>p.BenchmarkValue.HasValue)?"":" 当前缺少所选指数的同日起点报价或报告币种汇率。")+(curve.Points.Count(p=>p.NetAssets.HasValue)<2?" 当前不足两个完整净值点，无法连接净值曲线。":"");
    }
    [RelayCommand(CanExecute=nameof(CanDownloadBenchmark))]
    private async Task DownloadBenchmarkAsync(){forceMarketRefresh=true;try {await CalculateAsync();}finally{forceMarketRefresh=false;}}
    private bool forceMarketRefresh;
    public ObservableCollection<PerformanceAccountSelection> Accounts {get;}=[];
    public ObservableCollection<PerformanceMetricDisplay> Metrics {get;}=[];
    public ObservableCollection<PerformanceIssue> Issues {get;}=[];
    public ObservableCollection<PerformanceAccountResult> AccountResults {get;}=[];
    public ObservableCollection<PerformanceEventDisplay> Events {get;}=[];
    public ObservableCollection<PerformanceHistory> History {get;}=[];
    public ObservableCollection<InvestmentPortfolio> Portfolios {get;}=[];
    public ObservableCollection<PerformanceInputInfo> Sources {get;}=[];
    public ObservableCollection<PerformanceExposure> Exposure {get;}=[];
    public string[] Brokers {get;}=["IBKR","FUTU"];
    public string[] Currencies {get;}=["CNY","USD","HKD"];
    public string[] Periods {get;}=["自定义","本月","本年","上一自然年","近12个月","近3年","成立以来"];
    public string[] Methods {get;}=["精确流量时点 TWR","日末假定重建 TWR"];
    public PerformanceEventOption[] EventKinds {get;}=Enum.GetValues<InvestmentEventKind>().Select(k=>new PerformanceEventOption(k,EventName(k))).ToArray();
    [ObservableProperty] private string broker="IBKR";
    [ObservableProperty] private string currency="CNY";
    [ObservableProperty] private string period="自定义";
    [ObservableProperty] private DateTime from=DateTime.Today.AddYears(-1);
    [ObservableProperty] private DateTime to=DateTime.Today;
    [ObservableProperty] private int methodIndex;
    [ObservableProperty] private int fallbackDays;
    [ObservableProperty] private string summary="共用证券流水导入的数据，无需重复导入；缺少期初净值时，相应收益率显示不可计算。";
    [ObservableProperty] private string portfolioName="我的境外组合";
    [ObservableProperty] private InvestmentPortfolio? selectedPortfolio;
    [ObservableProperty] private PerformanceHistory? selectedHistory;
    [ObservableProperty] private PerformanceEventDisplay? selectedEvent;
    [ObservableProperty] private InvestmentEventKind classification=InvestmentEventKind.Deposit;
    [ObservableProperty] private string revisionReason="";
    [ObservableProperty] private bool classificationConfirmed;
    [ObservableProperty] private PerformanceEventDisplay? transferOut;
    [ObservableProperty] private PerformanceEventDisplay? transferIn;
    [ObservableProperty] private decimal transferTolerance;
    [ObservableProperty] private string transferReason="";
    [ObservableProperty] private string accountAlias="";
    [ObservableProperty] private PerformanceInputInfo? selectedSource;
    [ObservableProperty] private bool sourceIncluded=true;
    [ObservableProperty] private string sourceReason="";
    [ObservableProperty] private string accountCurrency="USD";
    [ObservableProperty] private PerformanceAccountSelection? manualAccount;
    [ObservableProperty] private DateTime navDate=DateTime.Today;
    [ObservableProperty] private decimal securitiesValue;
    [ObservableProperty] private decimal cashValue;
    [ObservableProperty] private decimal receivable;
    [ObservableProperty] private decimal liability;
    [ObservableProperty] private decimal payable;
    [ObservableProperty] private bool navComplete;
    [ObservableProperty] private string manualSource="";
    [ObservableProperty] private bool flowsComplete;
    [ObservableProperty] private bool incomeCostsComplete;
    [ObservableProperty] private string manualJson="";

    [ObservableProperty] private DateTime eventDate=DateTime.Today;
    [ObservableProperty] private InvestmentEventKind manualEventKind=InvestmentEventKind.Deposit;
    [ObservableProperty] private decimal eventAmount;
    [ObservableProperty] private string eventCurrency="USD";
    [ObservableProperty] private string eventSecurity="";
    [ObservableProperty] private string eventMarket="";
    [ObservableProperty] private decimal eventQuantity;
    [ObservableProperty] private decimal eventFee;
    [ObservableProperty] private decimal eventWithholding;
    public bool Available=>useCases is not null;
    public WorkspaceState State=>state;
    public PerformanceViewModel(WorkspaceState state,IPerformanceUseCases? useCases)
    {
        this.state=state;this.useCases=useCases;
        state.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(state.IsBusy))RefreshCommands();};
    }
    private bool CanRun()=>state.IsIdle && Available && !closed;
    private bool CanCalculate()=>CanRun() && Accounts.Any(a=>a.Selected);
    private bool CanExport()=>CanRun() && run is not null;
    private bool CanOpen()=>CanRun() && SelectedHistory is not null;
    private bool CanRevise()=>CanRun() && SelectedEvent is not null && RevisionReason.Trim().Length>0;
    private bool CanTransfer()=>CanRun() && TransferOut is not null && TransferIn is not null && TransferReason.Trim().Length>0;
    private void RefreshCommands()
    {AuthorizeOnlineUpdatesCommand.NotifyCanExecuteChanged();RefreshCurveCommand.NotifyCanExecuteChanged();DownloadBenchmarkCommand.NotifyCanExecuteChanged();RefreshCommand.NotifyCanExecuteChanged();CalculateCommand.NotifyCanExecuteChanged();SavePortfolioCommand.NotifyCanExecuteChanged();ExportCommand.NotifyCanExecuteChanged();ExportEncryptedCommand.NotifyCanExecuteChanged();OpenHistoryCommand.NotifyCanExecuteChanged();ReplayHistoryCommand.NotifyCanExecuteChanged();ReviseCommand.NotifyCanExecuteChanged();MatchTransferCommand.NotifyCanExecuteChanged();AddAccountCommand.NotifyCanExecuteChanged();RenameAccountCommand.NotifyCanExecuteChanged();AddNavCommand.NotifyCanExecuteChanged();ConfirmCoverageCommand.NotifyCanExecuteChanged();ImportManualCommand.NotifyCanExecuteChanged();AddEventCommand.NotifyCanExecuteChanged();ReviseSourceCommand.NotifyCanExecuteChanged();}
    private void Invalidate()
        =>InvalidateResult(true);
    private void InvalidateResult(bool parametersChanged)
    {
        if(rebuildingWorkspace)return;
        AccountDetails="";
        if(parametersChanged)parameterRevision++;
        run=null;NetAssetsChart=ChartPresentation.Empty;ComparisonChart=ChartPresentation.Empty;HasNetAssetsPoints=false;HasComparisonPoints=false;
        NetAssetsStatus=Accounts.Any(a=>a.Selected)?"统计范围或资料已改变，点击“更新曲线”即可重新计算。":"请先在绩效工作台选择账户，再点击“更新曲线”。";
        ComparisonStatus=NetAssetsStatus;CurveExplanation=NetAssetsStatus;
        Metrics.Clear();AccountResults.Clear();Issues.Clear();Exposure.Clear();Summary="统计范围或参数已改变，请重新计算；已存历史保留原范围。";RefreshCommands();
    }
    partial void OnCurrencyChanged(string value)=>Invalidate();
    partial void OnFromChanged(DateTime value)=>Invalidate();
    partial void OnToChanged(DateTime value)=>Invalidate();
    partial void OnMethodIndexChanged(int value)=>Invalidate();
    partial void OnFallbackDaysChanged(int value)=>Invalidate();
    partial void OnSelectedHistoryChanged(PerformanceHistory? value)=>RefreshCommands();
    partial void OnSelectedEventChanged(PerformanceEventDisplay? value)=>RefreshCommands();
    partial void OnRevisionReasonChanged(string value)=>RefreshCommands();
    partial void OnTransferReasonChanged(string value)=>RefreshCommands();
    partial void OnTransferOutChanged(PerformanceEventDisplay? value)=>RefreshCommands();
    partial void OnTransferInChanged(PerformanceEventDisplay? value)=>RefreshCommands();
    partial void OnSelectedPortfolioChanged(InvestmentPortfolio? value)
    {if(value is null)return;Currency=value.Currency;PortfolioName=value.Name;foreach(var a in Accounts)a.Selected=value.AccountIds.Contains(a.Account.Id);Invalidate();}
    partial void OnPeriodChanged(string value)
    {
        var today=DateTime.Today;To=today;
        From=value switch {"本月"=>new DateTime(today.Year,today.Month,1).AddDays(-1),"本年"=>new DateTime(today.Year,1,1).AddDays(-1),"上一自然年"=>new DateTime(today.Year-1,1,1).AddDays(-1),"近12个月"=>today.AddYears(-1),"近3年"=>today.AddYears(-3),_=>From};
        if(value=="上一自然年")To=new(today.Year-1,12,31);Invalidate();
    }
    private async Task Run(Func<CancellationToken,Task> action)
    {
        if(!CanRun())return;operation=new();
        try {await state.RunAsync(()=>action(operation.Token));}
        finally {operation.Dispose();operation=null;RefreshCommands();}
    }
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task RefreshAsync()=>await Run(Reload);
    private async Task Reload(CancellationToken token)
    {
        var next=await useCases!.LoadAsync(token);token.ThrowIfCancellationRequested();if(closed)return;
        var firstLoad=Accounts.Count==0;var previous=Accounts.Where(a=>a.Selected).Select(a=>a.Account.Id).ToHashSet();
        var nextSnapshotId=next.InputSnapshotId;
        var unchanged=workspace is not null && inputSnapshotId==nextSnapshotId;
        workspace=next;privacy=new(next.Data.Accounts,next.AccountIdentities);AccountDetails="";inputSnapshotId=nextSnapshotId;rebuildingWorkspace=true;
        try
        {
        Accounts.Clear();foreach(var a in workspace.Data.Accounts)Accounts.Add(new(a,Invalidate,privacy.Account(a)){Selected=firstLoad?a.Included:previous.Contains(a.Id)});
        Portfolios.Clear();foreach(var p in workspace.Portfolios)Portfolios.Add(p with{Name=privacy.Text(p.Name)});
        History.Clear();foreach(var h in workspace.History)History.Add(h with{Scope=privacy.Text(h.Scope)});
        Sources.Clear();foreach(var s in workspace.Sources)Sources.Add(s with{Name=privacy.Text(s.Name)});
        Events.Clear();foreach(var e in workspace.Data.Events)
        {
            var revision=workspace.Data.Revisions.Where(r=>r.EventId==e.Id).OrderBy(r=>r.CreatedAt).ThenBy(r=>r.Id).LastOrDefault();
            Events.Add(new(e.Id,privacy.Text(workspace.Data.Accounts.FirstOrDefault(a=>a.Id==e.AccountId)?.Alias??"未绑定账户"),e.Date.ToString("yyyy-MM-dd"),EventName(revision?.Kind??e.Kind)+(revision is {Confirmed:false}?"（待确认）":""),e.Amount,e.Currency,e.Source.DocumentId,e.Source.RowIdentity,e.Security));
        }
        ManualAccount=Accounts.FirstOrDefault();
        }
        finally {rebuildingWorkspace=false;}
        if(!unchanged)InvalidateResult(false);else RefreshCommands();
        state.Status=$"绩效资料：{Accounts.Count} 个账户、{Events.Count} 条事件、{workspace.Data.Valuations.Length} 个净值快照；未自动认定完整。";
    }
    [RelayCommand(CanExecute=nameof(CanCalculate))] private async Task CalculateAsync()=>await Run(async token=>
    {
        var request=new PerformanceRequest(Accounts.Where(a=>a.Selected).Select(a=>a.Account.Id).ToArray(),DateOnly.FromDateTime(From),DateOnly.FromDateTime(To),Currency,(ReturnMethod)MethodIndex,FallbackDays,SinceInception:Period=="成立以来");
        var revision=parameterRevision;
        string marketMessage="";
        if(OnlineUpdatesAllowed)
            try {await useCases!.FetchBenchmarkAsync(Benchmark,request,token,forceMarketRefresh);} catch(CrsException error) {marketMessage=error.Message;}
        token.ThrowIfCancellationRequested();if(closed || parameterRevision!=revision)return;
        var result=await useCases!.CalculateAsync(request,token);await Reload(token);
        token.ThrowIfCancellationRequested();
        if(closed || parameterRevision!=revision || result.InputHash!=inputSnapshotId)return;
        Display(result);await UpdateCurve(token);if(run==result)CurveExplanation+=" "+marketMessage;
    });
    private void Display(PerformanceRun value)
    {
        if(closed)return;
        var displayPrivacy=new PerformanceAccountPrivacy(value.Input.Accounts.Concat(workspace?.Data.Accounts??[]),workspace?.AccountIdentities??[]);
        run=value;Metrics.Clear();foreach(var m in value.Result.Metrics)Metrics.Add(new(m.Name,m.Value is null?"不可计算":m.Unit=="比例"?$"{m.Value:P4}":$"{m.Value:N2} {value.Result.Request.Currency}",Quality(m.Quality),m.Explanation));
        Issues.Clear();foreach(var issue in value.Result.Issues)Issues.Add(issue with{AccountAlias=displayPrivacy.Text(issue.AccountAlias)});AccountResults.Clear();foreach(var a in value.Result.Accounts)AccountResults.Add(a with{AccountAlias=displayPrivacy.Text(a.AccountAlias)});
        Exposure.Clear();foreach(var e in value.Result.Exposure)Exposure.Add(e with{AccountAlias=displayPrivacy.Text(e.AccountAlias)});
        Summary=$"{value.Result.EffectiveFrom:yyyy-MM-dd} → {value.Result.Request.To:yyyy-MM-dd} · {value.Result.Request.Currency} · {string.Join("、",value.Result.Accounts.Select(a=>displayPrivacy.Text(a.AccountAlias)))} · 每项指标独立标记质量 · {value.Id[..8]}";RefreshCommands();
    }
    private static string Quality(PerformanceQuality q)=>q switch {PerformanceQuality.Verified=>"已核对",PerformanceQuality.Reconstructed=>"可重建",PerformanceQuality.Estimated=>"估算",_=>"不可计算"};
    private static string EventName(InvestmentEventKind k)=>k switch {InvestmentEventKind.Deposit=>"外部入金",InvestmentEventKind.Withdrawal=>"外部出金",InvestmentEventKind.CashTransfer=>"账户现金转账",InvestmentEventKind.SecurityTransfer=>"证券实物转仓",InvestmentEventKind.Buy=>"买入",InvestmentEventKind.Sell=>"卖出",InvestmentEventKind.Dividend=>"股息",InvestmentEventKind.Interest=>"利息",InvestmentEventKind.WithholdingTax=>"预扣税",InvestmentEventKind.Fee=>"其他费用",InvestmentEventKind.FinancingInterest=>"融资利息",InvestmentEventKind.FxExchange=>"内部换汇",InvestmentEventKind.Borrowing=>"借款本金",InvestmentEventKind.Split=>"拆合股",InvestmentEventKind.Unsupported=>"暂不支持",_=>"未分类"};
    [RelayCommand(CanExecute=nameof(CanCalculate))] private async Task SavePortfolioAsync()=>await Run(async token=>{await useCases!.SavePortfolioAsync(new(SelectedPortfolio?.Id??Guid.NewGuid().ToString("N"),PortfolioName,Accounts.Where(a=>a.Selected).Select(a=>a.Account.Id).ToArray(),Currency),token);await Reload(token);});
    [RelayCommand(CanExecute=nameof(CanOpen))] private async Task OpenHistoryAsync()=>await Run(async token=>{var revision=parameterRevision;var value=await useCases!.OpenRunAsync(SelectedHistory!.Id,token);token.ThrowIfCancellationRequested();if(closed || parameterRevision!=revision)return;Display(value);await UpdateCurve(token);});
    [RelayCommand(CanExecute=nameof(CanOpen))] private async Task ReplayHistoryAsync()=>await Run(async token=>{var revision=parameterRevision;var result=await useCases!.ReplayAsync(SelectedHistory!.Id,token);await Reload(token);token.ThrowIfCancellationRequested();if(closed || parameterRevision!=revision)return;Display(result);await UpdateCurve(token);});
    [RelayCommand(CanExecute=nameof(CanRevise))] private async Task ReviseAsync()=>await Run(async token=>{await useCases!.ReviseClassificationAsync(SelectedEvent!.Id,Classification,ClassificationConfirmed,RevisionReason,token);await Reload(token);RevisionReason="";});
    [RelayCommand(CanExecute=nameof(CanTransfer))] private async Task MatchTransferAsync()=>await Run(async token=>{await useCases!.ConfirmTransferAsync(TransferOut!.Id,TransferIn!.Id,TransferTolerance,TransferReason,Currency,token);await Reload(token);TransferReason="";});
    // 确认缺失基础币种后才允许用基础币种输入整体净值，原币结单组成保持不变。
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task ConfirmAccountCurrencyAsync()=>await Run(async token=>{if(ManualAccount is null)throw new CrsException("请选择账户。");await useCases!.ConfirmBaseCurrencyAsync(ManualAccount.Account.Id,AccountCurrency,token);await Reload(token);});
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task AddAccountAsync()=>await Run(async token=>
    {
        if(string.IsNullOrWhiteSpace(AccountAlias))throw new CrsException("请填写境外账户别名。");
        await Manual(new(){Accounts=[new(Guid.NewGuid().ToString("N"),Broker,AccountAlias,AccountCurrency,DateOnly.FromDateTime(From))]},token);AccountAlias="";await Reload(token);
    });
    private Task<bool> Manual(PerformanceData data,CancellationToken token)=>useCases!.ImportManualAsync(System.Text.Json.JsonSerializer.Serialize(data,new System.Text.Json.JsonSerializerOptions{Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}}),token);
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task RenameAccountAsync()=>await Run(async token=>{if(ManualAccount is null)throw new CrsException("请选择账户。");await useCases!.RenameAccountAsync(ManualAccount.Account.Id,AccountAlias,token);await Reload(token);});
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task ReviseSourceAsync()=>await Run(async token=>{if(SelectedSource is null)throw new CrsException("请选择来源。");await useCases!.SetSourceIncludedAsync(SelectedSource.Hash,SourceIncluded,SourceReason,token);await Reload(token);});
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task AddEventAsync()=>await Run(async token=>
    {
        if(ManualAccount is null || string.IsNullOrWhiteSpace(ManualSource))throw new CrsException("资金/证券事件必须选择账户并填写原始来源。");
        var date=DateOnly.FromDateTime(EventDate);
        await Manual(new(){Events=[new(Guid.NewGuid().ToString("N"),ManualAccount.Account.Id,new(date.ToDateTime(new(12,0)),TimeSpan.Zero),date,ManualEventKind,EventAmount,EventCurrency,new("",ManualSource),EventSecurity,EventMarket,EventQuantity,EventFee,EventWithholding,Confirmed:ClassificationConfirmed)]},token);await Reload(token);
    });
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task AddNavAsync()=>await Run(async token=>
    {
        if(ManualAccount is null || !NavComplete || string.IsNullOrWhiteSpace(ManualSource))throw new CrsException("请选择账户，填写来源，并明确确认本日资产/负债完整。");
        if(!ManualAccount.Account.BaseCurrencyConfirmed)throw new CrsException("请先核对并确认该账户基础币种，不能按预设币种补录整体净值。");
        if(SecuritiesValue<0 || CashValue<0 || Receivable<0 || Liability<0 || Payable<0)throw new CrsException("资产、应收和负债金额不能为负；负现金请填融资负债。");
        var date=DateOnly.FromDateTime(NavDate);var c=ManualAccount.Account.BaseCurrency;var components=new List<ValuationComponent>{new("Cash",c,CashValue),new("Receivable",c,Receivable),new("Liability",c,Liability),new("Payable",c,Payable)};
        if(SecuritiesValue>0)components.Add(new("Security",c,SecuritiesValue));
        await Manual(new(){Valuations=[new(Guid.NewGuid().ToString("N"),ManualAccount.Account.Id,new(date.ToDateTime(new(23,59,59)),TimeSpan.Zero),date,ValuationMoment.EndOfDay,components.ToArray(),new("",ManualSource),PerformanceQuality.Estimated)]},token);await Reload(token);NavComplete=false;
    });
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task ConfirmCoverageAsync()=>await Run(async token=>
    {
        if(ManualAccount is null || !FlowsComplete || string.IsNullOrWhiteSpace(ManualSource))throw new CrsException("完整性确认需要账户、来源和明确勾选，不能将缺失资金流水视为零。");
        await Manual(new(){Coverage=[new(ManualAccount.Account.Id,DateOnly.FromDateTime(From),DateOnly.FromDateTime(To),true,new("",ManualSource),IncomeComplete:IncomeCostsComplete,CostsComplete:IncomeCostsComplete)]},token);await Reload(token);FlowsComplete=false;IncomeCostsComplete=false;
    });
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task ImportManualAsync()=>await Run(async token=>{await useCases!.ImportManualAsync(ManualJson,token);ManualJson="";await Reload(token);});
    [RelayCommand(CanExecute=nameof(CanExport))] private async Task ExportAsync()=>await Export(false);
    [RelayCommand(CanExecute=nameof(CanExport))] private async Task ExportEncryptedAsync()=>await Export(true);
    private async Task Export(bool encrypted)
    {
        var expectedRun=run;if(expectedRun is null)return;
        if(!encrypted && !state.Interaction.ConfirmPlaintextExport())return;
        var path=state.Interaction.SaveFile(encrypted?"加密底稿|*.crsexport":"Excel 报表|*.xlsx",encrypted?"绩效底稿.crsexport":"绩效报告.xlsx");if(path is null)return;
        if(closed || run!=expectedRun)return;
        var id=expectedRun.Id;var password=encrypted?state.DocumentEncryptionPassword:""; if(encrypted && password.Length is <12 or >1024) {state.Status="请先在设置中统一配置文档加密密码。";return;}await Run(token=>useCases!.ExportAsync(id,path,encrypted,password,encrypted?null:new(true),token));
    }
    [RelayCommand] private void Cancel()=>operation?.Cancel();
    public void ClearForLock()
    {
        // 选择项与补录表单也持有敏感引用，不能只清空列表和密码。
        closed=true;UseOffline();privacy=new([],[]);AccountDetails="";parameterRevision++;inputSnapshotId="";operation?.Cancel();ManualJson="";ManualSource="";AccountAlias="";RevisionReason="";TransferReason="";SourceReason="";
        SelectedHistory=null;SelectedEvent=null;TransferOut=null;TransferIn=null;ManualAccount=null;SelectedPortfolio=null;SelectedSource=null;
        SecuritiesValue=CashValue=Receivable=Liability=Payable=EventAmount=EventQuantity=EventFee=EventWithholding=0;EventSecurity="";EventMarket="";
        Accounts.Clear();Events.Clear();Metrics.Clear();Issues.Clear();History.Clear();Portfolios.Clear();Sources.Clear();AccountResults.Clear();Exposure.Clear();workspace=null;run=null;NetAssetsChart=ChartPresentation.Empty;ComparisonChart=ChartPresentation.Empty;HasNetAssetsPoints=false;HasComparisonPoints=false;NetAssetsStatus=ComparisonStatus=CurveExplanation="数据已锁定。";Summary="保险库已锁定，绩效资料已清理。";RefreshCommands();
    }
}
