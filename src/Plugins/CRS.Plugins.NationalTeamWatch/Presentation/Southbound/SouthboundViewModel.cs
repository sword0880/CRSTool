using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>只展示后台年度曲线；年份勾选不联网，不计算买入金额或填补缺日。</summary>
public partial class SouthboundViewModel:ObservableObject
{
    private readonly ISouthboundWorkspace workspace;
    private readonly CancellationToken lifetime;
    private CancellationTokenSource? operation;
    private bool initialized;
    private int version;
    private Guid? requestId;
    public SouthboundAnnualReport? Report {get;private set;}
    public IReadOnlyList<SouthboundYearSelection> Years {get;}
    public bool CanManage=>workspace.CanManage;
    [ObservableProperty]private bool busy;
    [ObservableProperty]private bool hasValues;
    [ObservableProperty]private string status="等待查询南向买入历史。";
    [ObservableProperty]private string caption="每年一条折线，按相同月日对齐；单位：亿港元。";
    [ObservableProperty]private string taskStatus="";
    [ObservableProperty]private ISeries[] series=[];
    public Axis[] XAxes {get;}=[new(){Name="月日",MinLimit=0,MaxLimit=365,MinStep=1,
        CustomSeparators=Enumerable.Range(1,12).Select(m=>(double)new DateOnly(2000,m,1).DayOfYear-1).ToArray(),
        Labeler=x=>x is >=0 and <=365?new DateOnly(2000,1,1).AddDays((int)Math.Round(x)).ToString("MM-dd"):"",TextSize=12}];
    public Axis[] YAxes {get;}=[new(){Name="买入金额（亿港元）",MinLimit=0,Labeler=y=>y.ToString("N0"),TextSize=12}];
    private static readonly SKColor[] Colors=[SKColor.Parse("#2563EB"),SKColor.Parse("#D97706"),SKColor.Parse("#059669"),SKColor.Parse("#9333EA"),SKColor.Parse("#DC2626"),SKColor.Parse("#0891B2"),SKColor.Parse("#BE185D"),SKColor.Parse("#4F46E5"),SKColor.Parse("#64748B"),SKColor.Parse("#65A30D")];
    public SouthboundViewModel(ISouthboundWorkspace workspace,CancellationToken lifetime)
    {
        this.workspace=workspace;this.lifetime=lifetime;var current=CRS.Timing.BeijingClock.Now.Year;
        Years=Enumerable.Range(Math.Max(SouthboundAnnualComparison.EarliestYear,current-9),Math.Min(10,current-SouthboundAnnualComparison.EarliestYear+1))
            .Reverse().Select(y=>new SouthboundYearSelection(y,y>=current-4,Rebuild)).ToArray();
    }
    partial void OnBusyChanged(bool value){QueryCommand.NotifyCanExecuteChanged();UpdateCommand.NotifyCanExecuteChanged();TaskCommand.NotifyCanExecuteChanged();}
    private bool CanQuery()=>!Busy;
    private bool CanUpdate()=>!Busy&&CanManage;
    private bool CanTask()=>!Busy&&requestId.HasValue;
    public async Task EnsureInitializedAsync(){if(initialized)return;initialized=true;await QueryAsync();}
    [RelayCommand(CanExecute=nameof(CanQuery))]
    private async Task QueryAsync()
    {
        Busy=true;var current=++version;Clear();Status="正在读取年度买入曲线…";
        using var pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=pending;pending.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var report=await workspace.SouthboundAnnualAsync(Years.Min(x=>x.Year),Years.Max(x=>x.Year),pending.Token);
            pending.Token.ThrowIfCancellationRequested();if(current!=version)return;
            Report=report;Caption=report.RetrievedAt is {} at?$"数据截至 {report.Through:yyyy-MM-dd} · 更新于 {at.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm} · 亿港元":"尚无已登记的南向买入历史，可点击更新数据。";Rebuild();
        }
        catch(OperationCanceledException){if(current==version){Clear();Status="查询已取消或超时；后台采集继续运行。";}}
        catch(Exception error) when(error is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or NotSupportedException)
        {if(current==version){Clear();Status="南向历史读取失败："+error.Message;}}
        finally{if(ReferenceEquals(operation,pending))operation=null;Busy=false;}
    }
    [RelayCommand(CanExecute=nameof(CanUpdate))]
    private Task UpdateAsync()=>ManageAsync(async token=>ShowReceipt(await workspace.QueueSouthboundAsync(token)));
    [RelayCommand(CanExecute=nameof(CanTask))]
    private Task TaskAsync()=>ManageAsync(async token=>ShowReceipt(await workspace.SouthboundStatusAsync(requestId!.Value,token)));
    private async Task ManageAsync(Func<CancellationToken,Task> action)
    {
        Busy=true;using var pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=pending;pending.CancelAfter(TimeSpan.FromSeconds(30));
        try{await action(pending.Token);}
        catch(OperationCanceledException){TaskStatus="更新受理或状态尚未确认，请沿原请求核查；已受理的后台任务继续运行。";}
        catch(Exception error) when(error is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or NotSupportedException){TaskStatus=error.Message;}
        finally{if(ReferenceEquals(operation,pending))operation=null;Busy=false;}
    }
    private void ShowReceipt(SouthboundCommandReceipt receipt)
    {
        requestId=receipt.RequestId;TaskCommand.NotifyCanExecuteChanged();
        TaskStatus=receipt.Code=="RetryDeferred"?"更新等待来源恢复，请沿原任务查看进度。"
            :receipt.State=="Completed"&&receipt.Code=="Succeeded"?"更新完成，点击“刷新曲线”读取最新数据。"
            :receipt.State=="Completed"?"更新已结束（"+receipt.Code+"），请到任务中心核查。":"更新已受理，后台正在采集；可点击“查看更新进度”。";
    }
    [RelayCommand]public void Cancel(){version++;operation?.Cancel();Clear();Status="已取消等待；后台已受理的采集继续运行。";}
    private void Clear(){Report=null;Series=[];HasValues=false;Caption="每年一条折线，按相同月日对齐；单位：亿港元。";}
    private void Rebuild()
    {
        if(Report is null)return;
        var selected=Years.Where(x=>x.IsSelected).Select(x=>x.Year).ToHashSet();
        Series=Report.Years.Where(x=>selected.Contains(x.Year)&&x.Points.Any(p=>p.BuyYiHkd.HasValue)).Select(curve=>
        {
            var values=curve.Points.Where(p=>p.BuyYiHkd.HasValue).Select(p=>new ObservablePoint(p.CalendarDay,(double)p.BuyYiHkd!.Value)).ToArray();
            return (ISeries)new LineSeries<ObservablePoint>{Name=curve.Year+"年",Values=values,Fill=null,LineSmoothness=0,
                GeometrySize=values.Length==1?5:0,Stroke=new SolidColorPaint(Colors[(curve.Year-SouthboundAnnualComparison.EarliestYear)%Colors.Length],2),
                YToolTipLabelFormatter=p=>p.Context.DataSource is ObservablePoint point&&point.X is {} day&&point.Y is {} value
                    ?$"{curve.Year}-{new DateOnly(2000,1,1).AddDays((int)day):MM-dd}  {value:N2} 亿港元":"无有效数据"};
        }).ToArray();
        HasValues=Series.Length>0;
        var noData=Report.Years.Where(x=>selected.Contains(x.Year)&&!x.Points.Any(p=>p.BuyYiHkd.HasValue)).Select(x=>x.Year).ToArray();
        Status=selected.Count==0?"请勾选要比较的年份。":!HasValues?"所选年度暂无可用数据，请更新数据或选择其他年份。"
            :noData.Length>0?$"{string.Join("、",noData)}年暂无可用数据；其余年度已显示。":"悬停查看实际日期与金额；休市和缺日不补值，连线连接已取得的观测点。";
        var missing=Report.Years.Where(x=>selected.Contains(x.Year)).Sum(x=>x.Points.Count(p=>!p.BuyYiHkd.HasValue));
        if(missing>0)Status+=$" {missing}个已登记日期缺少完整沪深金额，未绘制对应点。";
    }
}

public partial class SouthboundYearSelection(int year,bool selected,Action changed):ObservableObject
{
    public int Year {get;}=year;
    public string Label=>Year+"年";
    [ObservableProperty]private bool isSelected=selected;
    partial void OnIsSelectedChanged(bool value)=>changed();
}
