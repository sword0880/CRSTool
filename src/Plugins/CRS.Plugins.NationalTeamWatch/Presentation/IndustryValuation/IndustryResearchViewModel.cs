using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>只投影后台公布序列及任务回执；不联网、不计算分位、不打开市场库。</summary>
public partial class IndustryResearchViewModel(IIndustryValuationWorkspace workspace,CancellationToken lifetime):ObservableObject
{
    private CancellationTokenSource? operation;
    private bool initialized;
    private IndustryPeReport? report;
    private Guid? requestId;
    public ObservableCollection<IndustrySeriesSelection> Rows {get;}=[];
    /// <summary>同一报告的只读研究排序，不改变后台证据和勾选选择。</summary>
    public ObservableCollection<IndustrySeriesSelection> ComparisonRows {get;}=[];
    public IReadOnlyList<string> Metrics {get;}=["公布滚动PE · 加权平均","公布滚动PE · 中位数"];
    public bool CanQuery=>!Busy;
    public bool CanManage=>workspace.CanManage&&!Busy;
    [ObservableProperty] private DateTime? from=CRS.Timing.BeijingClock.Now.Date.AddDays(-400);
    [ObservableProperty] private DateTime? through=CRS.Timing.BeijingClock.Now.Date;
    [ObservableProperty] private bool refreshSource;
    [ObservableProperty] private string metric="公布滚动PE · 加权平均";
    [ObservableProperty] private string caption="尚未读取正式行业资料。";
    [ObservableProperty] private bool onlyDualLow;
    [ObservableProperty] private string comparisonSummary="尚未生成行业历史位置比较。";
    [ObservableProperty] private string status="公布PE较低不能单独证明低估；PB、市值覆盖率、价格及基本面仍需独立核对。";
    [ObservableProperty] private string taskStatus="尚未在此页提交行业采集；既有后台计划照常运行。";
    [ObservableProperty] private ISeries[] series=[];
    [ObservableProperty] private Axis[] xAxes=[];
    [ObservableProperty] private Axis[] yAxes=[];
    [ObservableProperty,NotifyPropertyChangedFor(nameof(CanQuery)),NotifyPropertyChangedFor(nameof(CanManage)),NotifyCanExecuteChangedFor(nameof(QueryCommand)),NotifyCanExecuteChangedFor(nameof(QueueCommand)),NotifyCanExecuteChangedFor(nameof(RefreshTaskCommand))]
    private bool busy;
    partial void OnMetricChanged(string value)=>Rebuild();
    partial void OnOnlyDualLowChanged(bool value)=>Rebuild();
    public async Task EnsureInitializedAsync(){if(initialized)return;initialized=true;await QueryAsync();}
    private (DateOnly From,DateOnly Through) Range()
    {
        if(From is not {} fromDate||Through is not {} throughDate)throw new InvalidDataException("请选择完整起止日期。");
        var fromDay=DateOnly.FromDateTime(fromDate);var throughDay=DateOnly.FromDateTime(throughDate);
        if(fromDay>throughDay||throughDay.DayNumber-fromDay.DayNumber>400||throughDay>DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.Date))throw new InvalidDataException("日期区间最多400个自然日，不能包含未来日期。");
        return(fromDay,throughDay);
    }
    [RelayCommand(CanExecute=nameof(CanQuery))]
    private async Task QueryAsync()
    {
        Busy=true;using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var range=Range();var next=await workspace.IndustryPeReportAsync(range.From,range.Through,current.Token);
            report=next;Rows.Clear();foreach(var row in next.Rows)Rows.Add(new(row,Rebuild));Rebuild();
            Caption=$"所选区间 {next.From:yyyy-MM-dd} 至 {next.Through:yyyy-MM-dd} · 知识截止 {next.Cutoff.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}";
            Status=next.CalendarVerified?"已核验交易日对照；缺日断线。分位仅比较本行业所选区间的公布序列，至少252个前置样本；不代表低估机会。":"交易日历尚未核验；展示已取得公布值，历史分位不可计算。";
        }
        catch(OperationCanceledException){Clear();Status="查询已取消或超时；后台采集继续运行，请稍后主动查询。";}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or TimeoutException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {Clear();Status="行业资料读取失败："+error.Message;}
        finally{operation=null;Busy=false;}
    }
    [RelayCommand(CanExecute=nameof(CanManage))]
    private async Task QueueAsync()
    {
        Busy=true;using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));
        try{var range=Range();ShowReceipt(await workspace.QueueIndustryPeAsync(range.From,range.Through,RefreshSource,current.Token));}
        catch(OperationCanceledException){TaskStatus="受理等待已取消或超时，不能确认采集结果；原请求身份已保留，请保持参数并核查。";}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or TimeoutException or InvalidDataException or InvalidOperationException or NotSupportedException){TaskStatus="行业采集尚未确认："+error.Message;}
        finally{operation=null;Busy=false;}
    }
    [RelayCommand(CanExecute=nameof(CanQuery))]
    private async Task RefreshTaskAsync()
    {
        if(requestId is not {} id){TaskStatus="本页没有已确认身份的行业任务；受理结果不明时请保持参数重新核查，或到任务中心查看。";return;}
        Busy=true;using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));
        try{ShowReceipt(await workspace.IndustryPeSyncStatusAsync(id,current.Token));}
        catch(OperationCanceledException){TaskStatus="任务查询已取消或超时；后台任务继续运行。";}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or TimeoutException or InvalidDataException or InvalidOperationException or NotSupportedException){TaskStatus="任务状态未知："+error.Message;}
        finally{operation=null;Busy=false;}
    }
    private void ShowReceipt(IndustryPeSyncReceipt receipt)
    {
        requestId=receipt.RequestId;
        TaskStatus=receipt.State=="Completed"&&receipt.Code=="Succeeded"?"采集命令已完成。请主动查询已登记资料；这不代表低估研究条件齐全。"
            :receipt.Code=="RetryDeferred"?"原请求正在等待来源恢复或冷却，无需重复提交；可在任务中心查看原因。"
            :receipt.Code=="NeedsReview"?"原件已取得，但存在差异待审核；对应行业日期保持不可计算。"
            // Completed仅代表调度终态；失败或超时不能继续显示已受理，也不能清除后台已提交的逐日事实。
            :receipt.State=="Completed"?$"采集命令已结束，结果为{receipt.Code}；请核查任务详情及已登记日期，不代表全部采集成功。"
            :receipt.Code is "Rejected" or "AlreadyRunning"?$"本次未新增可用采集任务（{receipt.Code}），请到任务中心核对原任务与输入。":"任务已受理，尚未确认采集完成；关闭页面不影响后台执行。";
    }
    [RelayCommand]private void Cancel()=>operation?.Cancel();
    private void Clear()
    {
        report=null;Rows.Clear();ComparisonRows.Clear();Series=[];XAxes=[];YAxes=[];
        Caption="本次未取得可用行业报告。";ComparisonSummary="本次未取得可用行业比较结果。";
    }
    private void Rebuild()
    {
        if(report is null){Series=[];ComparisonRows.Clear();return;}
        static int Priority(IndustryPeObservation observation)=>observation switch
        {
            IndustryPeObservation.DualHistoricalLow=>0,IndustryPeObservation.MetricDivergence=>1,
            IndustryPeObservation.SingleMetricLow=>2,IndustryPeObservation.NoDualLow=>3,_=>4
        };
        ComparisonRows.Clear();
        foreach(var item in Rows.Where(x=>!OnlyDualLow||x.Observation==IndustryPeObservation.DualHistoricalLow)
            .OrderBy(x=>Priority(x.Observation))
            .ThenBy(x=>Math.Max(x.Row.PublishedSeriesPercentile??101m,x.Row.PublishedMedianSeriesPercentile??101m))
            .ThenBy(x=>x.Row.Code,StringComparer.Ordinal))ComparisonRows.Add(item);
        var dual=Rows.Count(x=>x.Observation==IndustryPeObservation.DualHistoricalLow);
        var disagreement=Rows.Count(x=>x.Observation==IndustryPeObservation.MetricDivergence);
        var unknown=Rows.Count(x=>x.Observation==IndustryPeObservation.EvidenceIncomplete);
        ComparisonSummary=$"共{Rows.Count}个行业；双口径历史低位{dual}个、明显分歧{disagreement}个、证据不足{unknown}个；当前显示{ComparisonRows.Count}个。排序只用于研究，不是低估或买入排名。";
        var dates=report.Rows.FirstOrDefault()?.Curve.Select(x=>x.Date).ToArray()??[];
        Series=Rows.Where(x=>x.IsVisible&&(!OnlyDualLow||x.Observation==IndustryPeObservation.DualHistoricalLow)).Select(x=>(ISeries)new LineSeries<ObservablePoint>
        {
            Name=x.Row.Name,Fill=null,GeometrySize=2,LineSmoothness=0,EnableNullSplitting=true,
            Values=x.Row.Curve.Select((point,index)=>new ObservablePoint(index,(Metric==Metrics[0]?point.RollingWeighted:point.RollingMedian) is {} value?(double)value:null)).ToArray()
        }).ToArray();
        XAxes=[new(){Labels=dates.Select(x=>x.ToString("yyyy-MM-dd")).ToArray(),MinStep=1}];YAxes=[new(){Name=Metric}];
    }
}

/// <summary>选择仅改变可见曲线，所有金融状态与分位来自后台报告。</summary>
public partial class IndustrySeriesSelection(IndustryPeSeries row,Action changed):ObservableObject
{
    public IndustryPeSeries Row {get;}=row;
    public IndustryPeObservation Observation=>IndustryPePosition.Classify(Row);
    public string HistoricalPosition=>Observation switch
    {
        IndustryPeObservation.DualHistoricalLow=>"双口径历史低位（待核查）",
        IndustryPeObservation.MetricDivergence=>"加权与中位明显分歧",
        IndustryPeObservation.SingleMetricLow=>"仅单口径低位",
        IndustryPeObservation.NoDualLow=>"不满足双口径低位",
        _=>"历史证据不足"
    };
    [ObservableProperty]private bool isVisible=true;
    partial void OnIsVisibleChanged(bool value)=>changed();
    public string State=>Row.State switch
    {
        "NeedsReview"=>"同日差异待审", "CalendarUnavailable"=>"日历待核验", "PublishedSeriesResearch"=>"公布序列可研究，机会未知",
        "InsufficientComparableHistory"=>"前置历史样本不足", _=>"资料缺失，无法判断"
    };
}
