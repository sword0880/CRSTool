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
public sealed record PerformanceAccountOverviewRow(string Broker, string Alias, DateOnly? Date, string? Currency, decimal? NetAssets, decimal? Cash)
{
    public PerformanceCurrencyAmount[] CashByCurrency { get; init; } = [];
    public PerformanceCurrencyAmount[] NetAssetsByCurrency { get; init; } = [];
    public string? NetAssetsUnavailableReason { get; init; }
    public bool HasAmounts => Cash.HasValue || NetAssets.HasValue
        || CashByCurrency.Any(a => a.Amount.HasValue) || NetAssetsByCurrency.Any(a => a.Amount.HasValue);
    public string CashDisplay => Format(Cash, CashByCurrency, "暂无现金余额");
    public string NetAssetsDisplay => Format(NetAssets, NetAssetsByCurrency, NetAssetsUnavailableReason ?? "暂无估值");

    private static string Format(decimal? converted, PerformanceCurrencyAmount[] native, string unavailable)
    {
        if (converted is { } amount) return amount.ToString("N2");
        if (native.All(a => a.Amount is null)) return unavailable;
        return string.Join("\n", native.Select(a => $"{a.Currency} {(a.Amount is { } value ? value.ToString("N2") : "暂无数据")}"));
    }
}
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
    public string NetworkStatus=>OnlineUpdatesAllowed?"联网对比：获取指数历史行情及对比用汇率，仅发送指数、期间和币种。":"在线行情服务不可用，请检查连接后重试。";
    private void NotifyNetworkMode()
    {OnPropertyChanged(nameof(OnlineUpdatesAllowed));OnPropertyChanged(nameof(NetworkStatus));}
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
    [ObservableProperty] private string curveExplanation="联网获取基准指数及对比用汇率，与组合累计 TWR 按同币种、同起点对比；缺少估值证据时不生成收益率。";
    public string[] Benchmarks {get;}=["SSE","SPX","HSI","IXIC"];
    [ObservableProperty] private string benchmark="SSE";
    partial void OnBenchmarkChanged(string value) {parameterRevision++;ComparisonChart=ChartPresentation.Empty;HasComparisonPoints=false;ComparisonStatus="指数已切换，请点击“更新曲线”联网获取并重新计算。";CurveExplanation=ComparisonStatus;}
    private bool CanRefreshCurve()=>CanCalculate();
    [RelayCommand(CanExecute=nameof(CanRefreshCurve))]
    private async Task RefreshCurveAsync()
    {await CalculateAsync();}
    private async Task UpdateCurve(PerformanceRun expectedRun, string expectedBenchmark, long revision, CancellationToken token)
    {
        var curve=await useCases!.CurveAsync(expectedRun.Id,expectedBenchmark,token);
        token.ThrowIfCancellationRequested();
        if(closed || run!=expectedRun || parameterRevision!=revision || Benchmark!=expectedBenchmark)return;
        NetAssetsChart=DesktopChartAdapter.NetAssets(curve,expectedRun.Result.Request.Currency);ComparisonChart=DesktopChartAdapter.Comparison(curve);
        var navCount=curve.Points.Count(p=>p.NetAssets.HasValue);var unitCount=curve.Points.Count(p=>p.UnitValue.HasValue);var indexCount=curve.Points.Count(p=>p.BenchmarkValue.HasValue);
        HasNetAssetsPoints=navCount>0;HasComparisonPoints=unitCount>0 || indexCount>0;
        var reasons=expectedRun.Result.Issues.Where(i=>i.Code is "FX_MISSING" or "INITIAL_NAV_MISSING" || i.Code.Contains("VALUATION",StringComparison.Ordinal)).Select(i=>i.Explanation).Distinct().Take(2);
        NetAssetsStatus=navCount==0?"没有可计算的净资产点。请检查“资金流与异常检查”中的净值及估值日汇率问题。 "+string.Join(" ",reasons)
            :navCount==1?"目前只有 1 个有效净值点，只能显示单点；至少 2 个不同日期的完整估值才能画折线。":$"已显示 {navCount} 个净资产点。";
        ComparisonStatus=$"组合净值 {unitCount} 个有效点，指数 {indexCount} 个有效点。"+(unitCount<2?" 组合 TWR 需要完整期初、期末净值及资金流证据。":"")+(indexCount<2?" 指数需要所选期间的报价和对比用汇率；请点击“更新曲线”重试。":"");
        CurveExplanation=curve.Explanation+(curve.Points.Any(p=>p.BenchmarkValue.HasValue)?"":" 当前缺少所选指数的同日起点报价或报告币种汇率。")+(curve.Points.Count(p=>p.NetAssets.HasValue)<2?" 当前不足两个完整净值点，无法连接净值曲线。":"");
    }
    public ObservableCollection<PerformanceAccountSelection> Accounts {get;}=[];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleAccountOverview), nameof(AccountOverviewTotals), nameof(AccountOverviewEmptyMessage))]
    private PerformanceAccountOverviewRow[] accountOverview = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleAccountOverview))]
    private bool showAccountsWithoutAmounts;
    public PerformanceAccountOverviewRow[] VisibleAccountOverview => ShowAccountsWithoutAmounts
        ? AccountOverview : AccountOverview.Where(a => a.HasAmounts).ToArray();
    public PerformanceAccountOverviewTotal[] AccountOverviewTotals => PerformanceAccountOverviewTotal.FromAccounts(AccountOverview);
    public string AccountOverviewEmptyMessage => AccountOverview.Length == 0
        ? "暂无证券账户，请先导入券商资料或在「资料补录」登记账户。"
        : "暂无有金额的账户，勾选「显示无金额账户」可查看全部。";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnnualAssetPoints), nameof(NoAnnualAssetPoints))]
    private ChartPresentation annualAssetsChart = ChartPresentation.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestAssetAmount), nameof(LatestAssetCaption), nameof(HasLatestAssetAmount), nameof(HasAnnualRateGaps))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLatestAssetAmountCommand))]
    private PerformanceAnnualAssetRow[] annualAssetRows = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestAssetAmount), nameof(LatestAssetAmountAction))]
    private bool showLatestAssetAmount;
    private PerformanceAnnualAssetRow? LatestAsset => AnnualAssetRows.FirstOrDefault(row => row.Date.HasValue);
    public bool HasLatestAssetAmount => LatestAsset?.CoveredCny.HasValue == true;
    public string LatestAssetAmount => LatestAsset?.CoveredCny is { } amount
        ? ShowLatestAssetAmount ? $"¥ {amount:N2}" : "¥ ••••••"
        : "—";
    public string LatestAssetAmountAction => ShowLatestAssetAmount ? "隐藏最新净资产金额" : "显示最新净资产金额";
    [RelayCommand(CanExecute = nameof(HasLatestAssetAmount))]
    private void ToggleLatestAssetAmount() => ShowLatestAssetAmount = !ShowLatestAssetAmount;
    public string LatestAssetCaption => LatestAsset is { Date: { } date } latest
        ? $"{date:yyyy-MM-dd} 日末 · 已计入 {latest.CoveredAccounts}/{latest.ExpectedAccounts} 个账户" +
            (latest.CoveredCny is null ? " · 暂无可折算金额" :
                latest.CoveredAccounts < latest.ExpectedAccounts ? " · 部分覆盖" : "")
        : "暂无日末估值";
    public bool HasAnnualRateGaps => AnnualAssetRows.Any(row => row.MissingRates);
    public bool HasAnnualAssetPoints => AnnualAssetsChart.Series.Length > 0;
    public bool NoAnnualAssetPoints => !HasAnnualAssetPoints;
    [ObservableProperty] private bool showAccountOverview;
    [ObservableProperty] private string accountBrokerSummary = "正在读取账户资料…";
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
    [ObservableProperty] private DateTime from=CRS.Timing.BeijingClock.Today.AddYears(-1);
    [ObservableProperty] private DateTime to=CRS.Timing.BeijingClock.Today;
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
    [ObservableProperty] private DateTime navDate=CRS.Timing.BeijingClock.Today;
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

    [ObservableProperty] private DateTime eventDate=CRS.Timing.BeijingClock.Today;
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
        this.state=state;this.useCases=useCases;useCases?.SetOnlineUpdatesAllowed(true);
        state.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(state.IsBusy))RefreshCommands();};
    }
    private bool CanRun()=>state.IsIdle && Available && !closed;
    private bool CanCalculate()=>CanRun() && Accounts.Any(a=>a.Selected);
    private bool CanExport()=>CanRun() && run is not null;
    private bool CanOpen()=>CanRun() && SelectedHistory is not null;
    private bool CanRevise()=>CanRun() && SelectedEvent is not null && RevisionReason.Trim().Length>0;
    private bool CanTransfer()=>CanRun() && TransferOut is not null && TransferIn is not null && TransferReason.Trim().Length>0;
    private void RefreshCommands()
    {RefreshCurveCommand.NotifyCanExecuteChanged();RefreshCommand.NotifyCanExecuteChanged();CalculateCommand.NotifyCanExecuteChanged();SavePortfolioCommand.NotifyCanExecuteChanged();ExportCommand.NotifyCanExecuteChanged();ExportEncryptedCommand.NotifyCanExecuteChanged();OpenHistoryCommand.NotifyCanExecuteChanged();ReplayHistoryCommand.NotifyCanExecuteChanged();ReviseCommand.NotifyCanExecuteChanged();MatchTransferCommand.NotifyCanExecuteChanged();AddAccountCommand.NotifyCanExecuteChanged();RenameAccountCommand.NotifyCanExecuteChanged();AddNavCommand.NotifyCanExecuteChanged();ConfirmCoverageCommand.NotifyCanExecuteChanged();ImportManualCommand.NotifyCanExecuteChanged();AddEventCommand.NotifyCanExecuteChanged();ReviseSourceCommand.NotifyCanExecuteChanged();}
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
        var today=CRS.Timing.BeijingClock.Today;To=today;
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

    private static string BrokerName(string broker) => broker switch
    {
        "FUTU" => "富途证券",
        "IBKR" => "盈透证券（IBKR）",
        null or "" => "未标注券商",
        _ => broker
    };

    internal void UpdateAnnualAssets(PerformanceAnnualAssets[] years, int currentYear)
    {
        ShowLatestAssetAmount = false;
        AnnualAssetsChart = DesktopChartAdapter.AnnualAssets(years, currentYear, BrokerName);
        AnnualAssetRows = years.OrderByDescending(year => year.Year).Select(AnnualAssetRow).ToArray();
    }

    private static PerformanceAnnualAssetRow AnnualAssetRow(PerformanceAnnualAssets year)
    {
        if (year.Date is null)
            return new(year.Year, null, null, "—", 0, 0, "缺日末估值", false);

        var covered = year.Brokers.Where(broker => broker.NetAssetsCny.HasValue).ToArray();
        var count = covered.Sum(broker => broker.CoveredAccounts);
        var expected = year.Brokers.Sum(broker => broker.ExpectedAccounts);
        var coverage = new List<string> { $"{count}/{expected} 个账户" };
        if (year.MissingYearEnd) coverage.Add("缺 12 月 31 日估值");
        foreach (var broker in year.Brokers.Where(broker => broker.CoveredAccounts < broker.ExpectedAccounts))
        {
            var reason = broker.MissingRates ? "缺汇率" : "缺同日完整估值";
            coverage.Add(broker.CoveredAccounts == 0 ? $"{BrokerName(broker.Broker)}{reason}"
                : $"{BrokerName(broker.Broker)} {broker.CoveredAccounts}/{broker.ExpectedAccounts}（{reason}）");
        }
        var amounts = covered.Select(broker => $"{BrokerName(broker.Broker)}  {broker.NetAssetsCny:N2}");
        return new(year.Year, year.Date, covered.Length == 0 ? null : covered.Sum(broker => broker.NetAssetsCny),
            covered.Length == 0 ? "—" : string.Join("\n", amounts),
            count, expected, string.Join("\n", coverage), year.Brokers.Any(broker => broker.MissingRates));
    }

    public async Task RefreshAccountBrokerSummaryAsync(CancellationToken cancellation)
    {
        if (closed || useCases is null) return;
        try
        {
            var data = (await useCases.LoadAsync(cancellation)).Data;
            if (closed || cancellation.IsCancellationRequested) return;
            var brokers = data.Accounts.GroupBy(a => BrokerName(a.Broker))
                .OrderBy(g => g.Key, StringComparer.CurrentCulture)
                .Select(g => $"{g.Key} {g.Count()} 个");
            AccountBrokerSummary = data.Accounts.Length == 0 ? "暂无证券账户，导入券商资料后会显示在这里。"
                : $"共 {data.Accounts.Length} 个账户 · {string.Join(" · ", brokers)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (closed || cancellation.IsCancellationRequested) return;
            state.UseCases.LogFailure(error.GetType().Name);
            AccountBrokerSummary = "账户资料暂不可用，可在投资绩效中刷新资料。";
        }
    }

    private async Task Reload(CancellationToken token)
    {
        var next=await useCases!.LoadAsync(token);token.ThrowIfCancellationRequested();if(closed)return;
        var today = DateOnly.FromDateTime(CRS.Timing.BeijingClock.Today);
        var overviewData = next.AccountOverviewValuations is { } valuations ? next.Data with { Valuations = valuations } : next.Data;
        var annualDates = PerformanceAccountValuations.AnnualDates(overviewData, today);
        var rates = await useCases.ReadCachedValuationRatesAsync(annualDates, token);
        var (latest, annual) = await Task.Run(() => (
            PerformanceAccountValuations.Latest(overviewData, today, token),
            PerformanceAccountValuations.Annual(overviewData, today, rates, token)), token);
        token.ThrowIfCancellationRequested();if(closed)return;
        var firstLoad=Accounts.Count==0;var previous=Accounts.Where(a=>a.Selected).Select(a=>a.Account.Id).ToHashSet();
        var nextSnapshotId=next.InputSnapshotId;
        var unchanged=workspace is not null && inputSnapshotId==nextSnapshotId;
        workspace=next;privacy=new(next.Data.Accounts,next.AccountIdentities);AccountDetails="";inputSnapshotId=nextSnapshotId;rebuildingWorkspace=true;
        try
        {
        Accounts.Clear();foreach(var a in workspace.Data.Accounts)Accounts.Add(new(a,Invalidate,privacy.Account(a)){Selected=firstLoad?a.Included:previous.Contains(a.Id)});
        AccountOverview = Accounts.Select(a =>
        {
            var value = latest[a.Account.Id];
            return new PerformanceAccountOverviewRow(BrokerName(a.Broker), a.Alias, value.Date, value.Currency, value.NetAssets, value.Cash)
            {
                CashByCurrency = value.CashByCurrency,
                NetAssetsByCurrency = value.NetAssetsByCurrency,
                NetAssetsUnavailableReason = value.NetAssetsUnavailableReason
            };
        }).OrderBy(a => a.Broker, StringComparer.CurrentCulture).ThenBy(a => a.Alias, StringComparer.CurrentCulture).ToArray();
        UpdateAnnualAssets(annual, today.Year);
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
        var benchmark=Benchmark;
        InvalidateResult(false);
        if(!OnlineUpdatesAllowed)throw new CrsException("在线行情服务不可用，无法更新绩效对比。");
        try {await useCases!.FetchBenchmarkAsync(benchmark,request,token,force:true);}
        catch(CrsException error)
        {
            if(!closed && parameterRevision==revision)NetAssetsStatus=ComparisonStatus=CurveExplanation="联网更新失败："+error.Message+" 请重试。";
            throw;
        }
        token.ThrowIfCancellationRequested();if(closed || parameterRevision!=revision)return;
        var result=await useCases!.CalculateAsync(request,token);await Reload(token);
        token.ThrowIfCancellationRequested();
        if(closed || parameterRevision!=revision || result.InputHash!=inputSnapshotId)return;
        Display(result);await UpdateCurve(result,benchmark,revision,token);
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
    [RelayCommand(CanExecute=nameof(CanOpen))]
    private async Task OpenHistoryAsync() => await Run(async token =>
    {
        var revision = parameterRevision;
        var benchmark = Benchmark;
        var value = await useCases!.OpenRunAsync(SelectedHistory!.Id, token);
        token.ThrowIfCancellationRequested();
        if (closed || parameterRevision != revision) return;
        Display(value);
        await UpdateCurve(value, benchmark, revision, token);
    });
    [RelayCommand(CanExecute=nameof(CanOpen))]
    private async Task ReplayHistoryAsync() => await Run(async token =>
    {
        var revision = parameterRevision;
        var benchmark = Benchmark;
        var result = await useCases!.ReplayAsync(SelectedHistory!.Id, token);
        await Reload(token);
        token.ThrowIfCancellationRequested();
        if (closed || parameterRevision != revision) return;
        Display(result);
        await UpdateCurve(result, benchmark, revision, token);
    });
    [RelayCommand(CanExecute=nameof(CanRevise))] private async Task ReviseAsync()=>await Run(async token=>{await useCases!.ReviseClassificationAsync(SelectedEvent!.Id,Classification,ClassificationConfirmed,RevisionReason,token);await Reload(token);RevisionReason="";});
    [RelayCommand(CanExecute=nameof(CanTransfer))] private async Task MatchTransferAsync()=>await Run(async token=>{await useCases!.ConfirmTransferAsync(TransferOut!.Id,TransferIn!.Id,TransferTolerance,TransferReason,Currency,token);await Reload(token);TransferReason="";});
    // 确认缺失基础币种后才允许用基础币种输入整体净值，原币结单组成保持不变。
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task ConfirmAccountCurrencyAsync()=>await Run(async token=>{if(ManualAccount is null)throw new CrsException("请选择账户。");await useCases!.ConfirmBaseCurrencyAsync(ManualAccount.Account.Id,AccountCurrency,token);await Reload(token);});
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task AddAccountAsync()=>await Run(async token=>
    {
        await useCases!.AddAccountAsync(new(Broker,AccountAlias,AccountCurrency,DateOnly.FromDateTime(From)),token);AccountAlias="";await Reload(token);
    });
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task RenameAccountAsync()=>await Run(async token=>{if(ManualAccount is null)throw new CrsException("请选择账户。");await useCases!.RenameAccountAsync(ManualAccount.Account.Id,AccountAlias,token);await Reload(token);});
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task ReviseSourceAsync()=>await Run(async token=>{if(SelectedSource is null)throw new CrsException("请选择来源。");await useCases!.SetSourceIncludedAsync(SelectedSource.Hash,SourceIncluded,SourceReason,token);await Reload(token);});
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task AddEventAsync()=>await Run(async token=>
    {
        await useCases!.AddEventAsync(new(ManualAccount?.Account.Id,DateOnly.FromDateTime(EventDate),ManualEventKind,EventAmount,EventCurrency,
            ManualSource,EventSecurity,EventMarket,EventQuantity,EventFee,EventWithholding,ClassificationConfirmed),token);await Reload(token);
    });
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task AddNavAsync()=>await Run(async token=>
    {
        await useCases!.AddValuationAsync(new(ManualAccount?.Account.Id,DateOnly.FromDateTime(NavDate),SecuritiesValue,CashValue,Receivable,
            Liability,Payable,NavComplete,ManualSource),token);await Reload(token);NavComplete=false;
    });
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task ConfirmCoverageAsync()=>await Run(async token=>
    {
        await useCases!.ConfirmCoverageAsync(new(ManualAccount?.Account.Id,DateOnly.FromDateTime(From),DateOnly.FromDateTime(To),
            FlowsComplete,IncomeCostsComplete,ManualSource),token);await Reload(token);FlowsComplete=false;IncomeCostsComplete=false;
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
        closed=true;useCases?.SetOnlineUpdatesAllowed(false);NotifyNetworkMode();privacy=new([],[]);AccountDetails="";parameterRevision++;inputSnapshotId="";operation?.Cancel();ManualJson="";ManualSource="";AccountAlias="";RevisionReason="";TransferReason="";SourceReason="";
        SelectedHistory=null;SelectedEvent=null;TransferOut=null;TransferIn=null;ManualAccount=null;SelectedPortfolio=null;SelectedSource=null;
        SecuritiesValue=CashValue=Receivable=Liability=Payable=EventAmount=EventQuantity=EventFee=EventWithholding=0;EventSecurity="";EventMarket="";
        AccountOverview = []; ShowAccountsWithoutAmounts = false; AccountBrokerSummary = "";
        ShowLatestAssetAmount = false; AnnualAssetsChart = ChartPresentation.Empty; AnnualAssetRows = [];
        Accounts.Clear();Events.Clear();Metrics.Clear();Issues.Clear();History.Clear();Portfolios.Clear();Sources.Clear();AccountResults.Clear();Exposure.Clear();workspace=null;run=null;NetAssetsChart=ChartPresentation.Empty;ComparisonChart=ChartPresentation.Empty;HasNetAssetsPoints=false;HasComparisonPoints=false;NetAssetsStatus=ComparisonStatus=CurveExplanation="数据已锁定。";Summary="保险库已锁定，绩效资料已清理。";RefreshCommands();
    }
}
