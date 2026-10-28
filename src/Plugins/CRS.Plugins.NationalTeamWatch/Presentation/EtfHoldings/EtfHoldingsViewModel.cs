using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>展示配置ETF的目录及后台请求状态，不下载、解析或计算持仓。</summary>
public partial class EtfHoldingsViewModel(IEtfHoldingsWorkspace workspace,IEtfResearchWorkspace etfs,IExitAlertWorkspace evidence,CancellationToken lifetime):ObservableObject
{
    public EtfHoldingHistoryViewModel History {get;}=new(workspace,lifetime);
    private CancellationTokenSource? operation;
    private int generation;
    private bool settingFunds,indexHealthy;
    public bool CanManage=>workspace.CanManage;
    public ObservableCollection<EtfConfiguration> Funds{get;}=[];
    public ObservableCollection<EtfReportIndexDocument> Reports{get;}=[];
    public Task LoadTask{get;private set;}=Task.CompletedTask;
    [ObservableProperty] private EtfConfiguration? selectedFund;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(RegisterReportCommand))] private EtfReportIndexDocument? selectedReport;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(SearchReportsCommand)),NotifyCanExecuteChangedFor(nameof(RegisterReportCommand))] private bool busy;
    [ObservableProperty] private string status="选择已配置ETF，读取后台报告目录。";
    [ObservableProperty] private string capture="目录尚未读取，不能推定没有机构持仓。";
    [ObservableProperty] private DateTime? from=CRS.Timing.BeijingClock.Today.AddDays(-800);
    [ObservableProperty] private DateTime? through=CRS.Timing.BeijingClock.Today;
    private bool CanSearch=>CanManage&&!Busy&&SelectedFund is not null;
    private bool CanRegister=>CanSearch&&indexHealthy&&SelectedReport is not null;
    partial void OnSelectedFundChanged(EtfConfiguration? value)
    {
        History.Fund=value;
        if(settingFunds)return;
        SearchReportsCommand.NotifyCanExecuteChanged();
        if(value is null){Cancel();Reports.Clear();SelectedReport=null;indexHealthy=false;Status="请选择ETF。";return;}
        LoadTask=RunAsync(token=>ReadIndexAsync(value,token));
    }
    [RelayCommand]
    private Task Refresh()=>RunAsync(async token=>
    {
        var selected=SelectedFund;var rows=await etfs.ConfigurationAsync(token);token.ThrowIfCancellationRequested();
        settingFunds=true;
        try{Funds.Clear();foreach(var row in rows.Where(x=>x.Active))Funds.Add(row);SelectedFund=Funds.FirstOrDefault(x=>x.Market==selected?.Market&&x.Ticker==selected.Ticker)??Funds.FirstOrDefault();}
        finally{settingFunds=false;}
        SearchReportsCommand.NotifyCanExecuteChanged();
        if(SelectedFund is {} fund)await ReadIndexAsync(fund,token);
        else{Reports.Clear();SelectedReport=null;indexHealthy=false;Status="当前没有启用的监控ETF，请先调整ETF配置。";}
    });
    private async Task ReadIndexAsync(EtfConfiguration fund,CancellationToken token)
    {
        Reports.Clear();SelectedReport=null;indexHealthy=false;RegisterReportCommand.NotifyCanExecuteChanged();Status="正在读取已登记目录…";
        var result=await workspace.EtfReportIndexAsync(new(fund.Market,fund.Ticker),token);token.ThrowIfCancellationRequested();
        if(SelectedFund?.Market!=fund.Market||SelectedFund.Ticker!=fund.Ticker)return;
        if(result.Key!=new EtfKey(fund.Market,fund.Ticker))throw new InvalidDataException("ETF目录与所选证券不符。");
        foreach(var row in result.Documents)Reports.Add(row);
        indexHealthy=result.State=="IndexedUnreviewed";RegisterReportCommand.NotifyCanExecuteChanged();
        Status=result.State switch{"NotSearched"=>"尚未检索官方目录；未知不代表没有披露。","IndexedUnreviewed"=>$"已登记{Reports.Count}条报告线索，均未批准为机构持仓。",_=>"目录原件或来源不可用；保留历史线索，禁止据此登记新证据。"};
        Capture=result.SearchId is null?"无已完成的目录检索。":$"区间 {result.From:yyyy-MM-dd} 至 {result.Through:yyyy-MM-dd} · 登记 {CRS.Timing.BeijingTime.Display(result.CapturedAt)} · 来源目录没有冻结快照，不能据此证明完整覆盖。";
    }
    [RelayCommand(CanExecute=nameof(CanSearch))]
    private Task SearchReports()=>RunAsync(async token=>
    {
        var fund=SelectedFund??throw new InvalidDataException("请选择ETF。");
        if(From is null||Through is null)throw new InvalidDataException("请输入查询起止日期。");
        var receipt=await workspace.QueueEtfReportsAsync(new(fund.Market,fund.Ticker),DateOnly.FromDateTime(From.Value),DateOnly.FromDateTime(Through.Value),token);
        token.ThrowIfCancellationRequested();
        Status=$"后台请求 {receipt.RequestId:N}：{receipt.State} / {receipt.Code}。受理不等于检索完成；请查看任务中心，完成后点击刷新。";
    });
    [RelayCommand(CanExecute=nameof(CanRegister))]
    private Task RegisterReport()=>RunAsync(async token=>
    {
        var fund=SelectedFund??throw new InvalidDataException("请选择ETF。");var report=SelectedReport??throw new InvalidDataException("请选择报告。");
        await evidence.RegisterAlertDocumentAsync(new(fund.Market,fund.Ticker,report.DocumentUrl),token);token.ThrowIfCancellationRequested();
        Status="原件登记请求已提交；请核对后台结果，再到ETF观察的预警中心读取原件候选并人工核验。登记不批准持仓或准确公布时点。";
    });
    private async Task RunAsync(Func<CancellationToken,Task> action)
    {
        operation?.Cancel();operation?.Dispose();var source=operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime);var version=++generation;Busy=true;
        try{await action(source.Token);}
        catch(OperationCanceledException){if(version==generation)Status="已取消等待；已受理的后台任务可能继续运行，请在任务中心核对。";}
        catch(Exception error){if(version==generation)Status=error is InvalidDataException or CRS.Worker.Ipc.BackgroundServiceException?error.Message:"目录操作不可用；类别："+error.GetType().Name;}
        finally{if(version==generation)Busy=false;}
    }
    [RelayCommand]
    public void Cancel(){generation++;operation?.Cancel();operation?.Dispose();operation=null;Busy=false;History.Cancel();Status="已取消页面等待；已受理后台任务须在任务中心核对。";}
}
