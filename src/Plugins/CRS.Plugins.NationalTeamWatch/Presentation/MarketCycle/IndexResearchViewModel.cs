using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>界面只呈现后台归一曲线及缺失状态；取消/改日期不撤销已经受理的后台采集。</summary>
public partial class IndexResearchViewModel(IMarketCycleWorkspace workspace,CancellationToken lifetime):ObservableObject
{
    public bool CanManage=>workspace.CanManage;
    // 保存后台报告身份供只读验收和后续核对；界面不重算曲线或改变金融指纹。
    public IndexSeriesResearchReport? Report {get;private set;}
    private bool initialized;private bool querying;private int queryVersion;private Guid? requestId;private string requestScope="";private CancellationTokenSource? operation;
    [ObservableProperty]private DateTime? from=CRS.Timing.BeijingClock.Now.Date.AddYears(-1);
    [ObservableProperty]private DateTime? through=CRS.Timing.BeijingClock.Now.Date;
    [ObservableProperty]private DateTime? snapshotDate=CRS.Timing.BeijingClock.Now.Date;
    [ObservableProperty]private bool busy;
    [ObservableProperty]private bool refreshSource;
    [ObservableProperty]private string status="等待查询已登记的指数资料。";
    [ObservableProperty]private string caption="价格、PB历史与覆盖均待核验。";
    [ObservableProperty]private string taskStatus="尚未在此页提交任务；现有后台计划继续运行。";
    [ObservableProperty]private string sourceStatus="来源用途许可待核验，当前只查询已登记资料。";
    private bool priceCollectionAllowed,valuationCollectionAllowed;
    [ObservableProperty]private ISeries[] series=[];
    // 无实际可见点时不显示图表默认的0刻度；资料缺失不呈现为价格为零。
    [ObservableProperty]private bool hasChartValues;
    [ObservableProperty]private Axis[] xAxes=[];
    [ObservableProperty]private Axis[] yAxes=[];
    public ObservableCollection<IndexResearchSelection> Rows {get;}=[];
    partial void OnFromChanged(DateTime? value)=>RangeChanged();
    partial void OnThroughChanged(DateTime? value)=>RangeChanged();
    partial void OnBusyChanged(bool value){QueryCommand.NotifyCanExecuteChanged();QueuePricesCommand.NotifyCanExecuteChanged();QueueValuationCommand.NotifyCanExecuteChanged();RefreshTaskCommand.NotifyCanExecuteChanged();}
    private void RangeChanged(){queryVersion++;if(querying)operation?.Cancel();Clear();Status="日期已改变，请主动查询所选区间。";}
    private bool CanQuery()=>!Busy;
    private bool CanCollectPrices()=>!Busy&&CanManage&&priceCollectionAllowed;
    private bool CanCollectValuation()=>!Busy&&CanManage&&valuationCollectionAllowed;
    private (DateOnly From,DateOnly Through) Range()
    {
        if(From is not {} start||Through is not {} end)throw new InvalidDataException("请选择起止日期。");var first=DateOnly.FromDateTime(start);var last=DateOnly.FromDateTime(end);
        if(first>last||last.DayNumber-first.DayNumber>400||last>DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime))throw new InvalidDataException("请选择400个自然日以内、截至今天的有效区间。");return(first,last);
    }
    public async Task EnsureInitializedAsync(){if(initialized)return;initialized=true;await QueryAsync();}
    [RelayCommand(CanExecute=nameof(CanQuery))]
    private async Task QueryAsync()
    {
        Busy=true;querying=true;var version=++queryVersion;using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var range=Range();var report=await workspace.IndexSeriesReportAsync(range.From,range.Through,current.Token);if(version!=queryVersion)return;
            Report=report;
            priceCollectionAllowed=report.PriceCollectionAllowed;valuationCollectionAllowed=report.ValuationCollectionAllowed;
            SourceStatus=priceCollectionAllowed&&valuationCollectionAllowed?"来源用途已通过后台准入核对；采集仍受来源冷却及数据完整性检查。":"来源用途许可尚未全部核验；未获准的采集操作保持关闭，既有资料可查询。";
            Rows.Clear();foreach(var row in report.Rows)Rows.Add(new(row,report.CalendarVerified,Rebuild));Rebuild();
            Caption=$"所选区间 {report.From:yyyy-MM-dd} 至 {report.Through:yyyy-MM-dd}；共同起点 {(report.BaseDate is {} basis?basis.ToString("yyyy-MM-dd"):"待核验")}；知识截止 {report.Cutoff.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}。";
            Status=report.CalendarVerified?"采用已核交易日对照；缺口、待审及损坏证明断线。PB历史、覆盖率及基本面不足，机会未评估。":"交易日历待核验，不能确认共同起点或250日指标。";
            var latest=report.Rows.Where(x=>x.LatestPriceDate.HasValue).Select(x=>x.LatestPriceDate!.Value).DefaultIfEmpty().Max();
            if(latest!=default&&SnapshotDate==CRS.Timing.BeijingClock.Now.Date)SnapshotDate=latest.ToDateTime(TimeOnly.MinValue);
        }
        catch(OperationCanceledException){if(version==queryVersion){Clear();Status="查询已取消或超时；后台采集继续运行。";}}
        catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {if(version==queryVersion){Clear();Status="指数资料读取失败："+error.Message;}}
        finally{operation=null;querying=false;Busy=false;}
    }
    [RelayCommand(CanExecute=nameof(CanCollectPrices))]
    private Task QueuePricesAsync()=>ManageAsync(async token=>
    {
        var range=Range();requestScope=$"日线 {range.From:yyyy-MM-dd}～{range.Through:yyyy-MM-dd}";ShowReceipt(await workspace.QueueIndexSeriesAsync(range.From,range.Through,RefreshSource,token));
    });
    [RelayCommand(CanExecute=nameof(CanCollectValuation))]
    private Task QueueValuationAsync()=>ManageAsync(async token=>
    {
        if(SnapshotDate is not {} day)throw new InvalidDataException("请选择估值统计日。");var date=DateOnly.FromDateTime(day);requestScope=$"估值统计日 {date:yyyy-MM-dd}";ShowReceipt(await workspace.QueueIndexValuationAsync(date,token));
    });
    [RelayCommand(CanExecute=nameof(CanQuery))]
    private Task RefreshTaskAsync()=>ManageAsync(async token=>
    {
        if(requestId is not {} id){TaskStatus="本页尚未确认任务身份；受理未知时请保持原参数重新核查，或到任务中心查看。";return;}ShowReceipt(await workspace.IndexSeriesStatusAsync(id,token));
    });
    private async Task ManageAsync(Func<CancellationToken,Task> action)
    {
        Busy=true;using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));
        try{await action(current.Token);}
        catch(OperationCanceledException){TaskStatus=requestScope+"：受理或状态等待已取消/超时，结果未确认；保持原参数和请求身份核查。";}
        catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or NotSupportedException){TaskStatus=requestScope+"：任务未确认，"+error.Message;}
        finally{operation=null;Busy=false;}
    }
    private void ShowReceipt(IndexSeriesCommandReceipt receipt)
    {
        requestId=receipt.RequestId;var message=receipt.Code=="RetryDeferred"?"原请求等待来源冷却或恢复，无需重复提交。"
            :receipt.Code=="NeedsReview"?"口径或差异需要核对，对应数据保持不可用。"
            :receipt.State=="Completed"&&receipt.Code=="Succeeded"?"采集命令已完成，请主动查询已登记资料；研究条件仍须独立核验。"
            :receipt.State=="Completed"?$"命令已结束，结果为{receipt.Code}，请到任务中心核查原因。"
            :receipt.Code is "Rejected" or "AlreadyRunning"?$"本次未确认新增采集（{receipt.Code}），请核对原任务。":"任务已受理，尚未确认完成；关闭页面不影响后台执行。";
        TaskStatus=requestScope+"："+message;
    }
    [RelayCommand]private void Cancel()=>operation?.Cancel();
    private void Clear(){Report=null;Rows.Clear();Series=[];HasChartValues=false;XAxes=[];YAxes=[];priceCollectionAllowed=false;valuationCollectionAllowed=false;SourceStatus="来源用途许可待核验，当前只查询已登记资料。";Caption="本次未取得可用的指数研究报告。";QueuePricesCommand.NotifyCanExecuteChanged();QueueValuationCommand.NotifyCanExecuteChanged();}
    private void Rebuild()
    {
        var dates=Rows.FirstOrDefault()?.Row.Curve.Select(x=>x.Date).ToArray()??[];
        HasChartValues=Rows.Any(x=>x.IsVisible&&x.Row.Curve.Any(point=>point.NormalizedLevel.HasValue));
        Series=Rows.Where(x=>x.IsVisible).Select(x=>(ISeries)new LineSeries<ObservablePoint>{Name=x.Row.Name+(x.Row.Family=="CniAShare"?"（参照）":""),Fill=null,GeometrySize=0,LineSmoothness=0,EnableNullSplitting=true,Values=x.Row.Curve.Select((point,index)=>new ObservablePoint(index,point.NormalizedLevel is {} value?(double)value:null)).ToArray()}).ToArray();
        XAxes=[new(){Labels=dates.Select(x=>x.ToString("yyyy-MM-dd")).ToArray(),MinStep=1}];YAxes=[new(){Name="共同起点100"}];
    }
}

public partial class IndexResearchSelection(IndexSeriesResearchRow row,bool calendarVerified,Action changed):ObservableObject
{
    public IndexSeriesResearchRow Row {get;}=row;
    [ObservableProperty]private bool isVisible=true;
    partial void OnIsVisibleChanged(bool value)=>changed();
    public string CoverageText=>calendarVerified?$"{Row.AcceptedPriceDays}/{Row.ExpectedPriceDays}":"日历待核验";
    public string PriceText=>Row.PriceState switch{"PublishedSeriesResearch"=>"公布序列可研究","MissingCommonBase"=>"共同起点缺失","CalendarUnavailable"=>"日历待核验","PriceGapsOrPending"=>"缺口或差异待核",_=>"暂无可核对价格"};
    public string ValuationText=>Row.ValuationState switch{"MissingPublishedSnapshot"=>"估值快照缺失","SnapshotPendingOrUnhealthy"=>"估值或价格证明待核",_=>"当期快照，PB历史/覆盖待核"};
}
