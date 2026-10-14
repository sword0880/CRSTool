using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application;
using CRS.Application.Performance;
using CRS.Domain.Performance;

namespace CRS.DesktopClient.ViewModels;

public sealed partial class PerformanceAccountSelection(InvestmentAccount account,Action changed) : ObservableObject
{
    public InvestmentAccount Account {get;}=account;
    public string Alias=>Account.Alias;
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
    [ObservableProperty] private string summary="导入境外结单或补录净值与资金流；成交记录不能代替期初净资产。";
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
    [ObservableProperty] private string exportPassword="";
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
    {RefreshCommand.NotifyCanExecuteChanged();ImportCommand.NotifyCanExecuteChanged();CalculateCommand.NotifyCanExecuteChanged();SavePortfolioCommand.NotifyCanExecuteChanged();ExportCommand.NotifyCanExecuteChanged();ExportEncryptedCommand.NotifyCanExecuteChanged();OpenHistoryCommand.NotifyCanExecuteChanged();ReplayHistoryCommand.NotifyCanExecuteChanged();ReviseCommand.NotifyCanExecuteChanged();MatchTransferCommand.NotifyCanExecuteChanged();AddAccountCommand.NotifyCanExecuteChanged();RenameAccountCommand.NotifyCanExecuteChanged();AddNavCommand.NotifyCanExecuteChanged();ConfirmCoverageCommand.NotifyCanExecuteChanged();ImportManualCommand.NotifyCanExecuteChanged();AddEventCommand.NotifyCanExecuteChanged();ReviseSourceCommand.NotifyCanExecuteChanged();}
    private void Invalidate() {run=null;Metrics.Clear();AccountResults.Clear();Issues.Clear();Exposure.Clear();Summary="统计范围或参数已改变，请重新计算；已存历史保留原范围。";RefreshCommands();}
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
        finally {operation.Dispose();operation=null;ExportPassword="";RefreshCommands();}
    }
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task RefreshAsync()=>await Run(Reload);
    private async Task Reload(CancellationToken token)
    {
        var firstLoad=Accounts.Count==0;var previous=Accounts.Where(a=>a.Selected).Select(a=>a.Account.Id).ToHashSet();var next=await useCases!.LoadAsync(token);token.ThrowIfCancellationRequested();if(closed)return;workspace=next;
        Accounts.Clear();foreach(var a in workspace.Data.Accounts)Accounts.Add(new(a,Invalidate){Selected=firstLoad?a.Included:previous.Contains(a.Id)});
        Portfolios.Clear();foreach(var p in workspace.Portfolios)Portfolios.Add(p);
        History.Clear();foreach(var h in workspace.History)History.Add(h);
        Sources.Clear();foreach(var s in workspace.Sources)Sources.Add(s);
        Events.Clear();foreach(var e in workspace.Data.Events)
        {
            var revision=workspace.Data.Revisions.Where(r=>r.EventId==e.Id).OrderBy(r=>r.CreatedAt).ThenBy(r=>r.Id).LastOrDefault();
            Events.Add(new(e.Id,workspace.Data.Accounts.FirstOrDefault(a=>a.Id==e.AccountId)?.Alias??"未绑定账户",e.Date.ToString("yyyy-MM-dd"),EventName(revision?.Kind??e.Kind)+(revision is {Confirmed:false}?"（待确认）":""),e.Amount,e.Currency,e.Source.DocumentId,e.Source.RowIdentity,e.Security));
        }
        ManualAccount=Accounts.FirstOrDefault();Invalidate();state.Status=$"绩效资料：{Accounts.Count} 个账户、{Events.Count} 条事件、{workspace.Data.Valuations.Length} 个净值快照；未自动认定完整。";
    }
    [RelayCommand(CanExecute=nameof(CanRun))] private async Task ImportAsync()
    {
        var paths=state.Interaction.PickFiles("绩效资料|*.json;*.xml;*.xlsx",true);if(paths.Length==0)return;
        await Run(async token=>{foreach(var path in paths)await useCases!.ImportAsync(path,Broker,token);await Reload(token);});
    }
    [RelayCommand(CanExecute=nameof(CanCalculate))] private async Task CalculateAsync()=>await Run(async token=>
    {
        var request=new PerformanceRequest(Accounts.Where(a=>a.Selected).Select(a=>a.Account.Id).ToArray(),DateOnly.FromDateTime(From),DateOnly.FromDateTime(To),Currency,(ReturnMethod)MethodIndex,FallbackDays,SinceInception:Period=="成立以来");
        var result=await useCases!.CalculateAsync(request,token);await Reload(token);Display(result);
    });
    private void Display(PerformanceRun value)
    {
        if(closed)return;
        run=value;Metrics.Clear();foreach(var m in value.Result.Metrics)Metrics.Add(new(m.Name,m.Value is null?"不可计算":m.Unit=="比例"?$"{m.Value:P4}":$"{m.Value:N2} {value.Result.Request.Currency}",Quality(m.Quality),m.Explanation));
        Issues.Clear();foreach(var issue in value.Result.Issues)Issues.Add(issue);AccountResults.Clear();foreach(var a in value.Result.Accounts)AccountResults.Add(a);
        Exposure.Clear();foreach(var e in value.Result.Exposure)Exposure.Add(e);
        Summary=$"{value.Result.EffectiveFrom:yyyy-MM-dd} → {value.Result.Request.To:yyyy-MM-dd} · {value.Result.Request.Currency} · {string.Join("、",value.Result.Accounts.Select(a=>a.AccountAlias))} · 每项指标独立标记质量 · {value.Id[..8]}";RefreshCommands();
    }
    private static string Quality(PerformanceQuality q)=>q switch {PerformanceQuality.Verified=>"已核对",PerformanceQuality.Reconstructed=>"可重建",PerformanceQuality.Estimated=>"估算",_=>"不可计算"};
    private static string EventName(InvestmentEventKind k)=>k switch {InvestmentEventKind.Deposit=>"外部入金",InvestmentEventKind.Withdrawal=>"外部出金",InvestmentEventKind.CashTransfer=>"账户现金转账",InvestmentEventKind.SecurityTransfer=>"证券实物转仓",InvestmentEventKind.Buy=>"买入",InvestmentEventKind.Sell=>"卖出",InvestmentEventKind.Dividend=>"股息",InvestmentEventKind.Interest=>"利息",InvestmentEventKind.WithholdingTax=>"预扣税",InvestmentEventKind.Fee=>"其他费用",InvestmentEventKind.FinancingInterest=>"融资利息",InvestmentEventKind.FxExchange=>"内部换汇",InvestmentEventKind.Borrowing=>"借款本金",InvestmentEventKind.Split=>"拆合股",InvestmentEventKind.Unsupported=>"暂不支持",_=>"未分类"};
    [RelayCommand(CanExecute=nameof(CanCalculate))] private async Task SavePortfolioAsync()=>await Run(async token=>{await useCases!.SavePortfolioAsync(new(SelectedPortfolio?.Id??Guid.NewGuid().ToString("N"),PortfolioName,Accounts.Where(a=>a.Selected).Select(a=>a.Account.Id).ToArray(),Currency),token);await Reload(token);});
    [RelayCommand(CanExecute=nameof(CanOpen))] private async Task OpenHistoryAsync()=>await Run(async token=>Display(await useCases!.OpenRunAsync(SelectedHistory!.Id,token)));
    [RelayCommand(CanExecute=nameof(CanOpen))] private async Task ReplayHistoryAsync()=>await Run(async token=>{var result=await useCases!.ReplayAsync(SelectedHistory!.Id,token);await Reload(token);Display(result);});
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
        if(!encrypted && !state.Interaction.ConfirmPlaintextExport())return;
        var path=state.Interaction.SaveFile(encrypted?"加密底稿|*.crsexport":"Excel 报表|*.xlsx",encrypted?"绩效底稿.crsexport":"绩效报告.xlsx");if(path is null)return;
        var id=run!.Id;var password=ExportPassword;await Run(token=>useCases!.ExportAsync(id,path,encrypted,password,encrypted?null:new(true),token));
    }
    [RelayCommand] private void Cancel()=>operation?.Cancel();
    public void ClearForLock()
    {
        // 选择项与补录表单也持有敏感引用，不能只清空列表和密码。
        closed=true;operation?.Cancel();ExportPassword="";ManualJson="";ManualSource="";AccountAlias="";RevisionReason="";TransferReason="";SourceReason="";
        SelectedHistory=null;SelectedEvent=null;TransferOut=null;TransferIn=null;ManualAccount=null;SelectedPortfolio=null;SelectedSource=null;
        SecuritiesValue=CashValue=Receivable=Liability=Payable=EventAmount=EventQuantity=EventFee=EventWithholding=0;EventSecurity="";EventMarket="";
        Accounts.Clear();Events.Clear();Metrics.Clear();Issues.Clear();History.Clear();Portfolios.Clear();Sources.Clear();AccountResults.Clear();Exposure.Clear();workspace=null;run=null;Summary="保险库已锁定，绩效资料已清理。";RefreshCommands();
    }
}
