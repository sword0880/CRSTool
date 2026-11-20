using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using LiveChartsCore;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>展示后台年度累计曲线与当日净额提示；不计算金融金额或填补缺日。</summary>
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
    [ObservableProperty]private string status="等待查询南向净买入历史。";
    [ObservableProperty]private string caption="每年一条折线，按相同月日对齐；单位：亿港元。";
    [ObservableProperty]private string taskStatus="";
    [ObservableProperty]private ISeries[] series=[];
    public SouthboundViewModel(ISouthboundWorkspace workspace,CancellationToken lifetime)
    {
        this.workspace=workspace;this.lifetime=lifetime;var current=CRS.Timing.BeijingClock.Now.Year;
        Years=Enumerable.Range(Math.Max(SouthboundAnnualComparison.EarliestYear,current-9),Math.Min(10,current-SouthboundAnnualComparison.EarliestYear+1))
            .Reverse().Select(y=>new SouthboundYearSelection(y,y>=current-4,Rebuild)).ToArray();
        XAxes[0].PropertyChanged+=OnZoomRangeChanged;
    }
    partial void OnBusyChanged(bool value){QueryCommand.NotifyCanExecuteChanged();UpdateCommand.NotifyCanExecuteChanged();TaskCommand.NotifyCanExecuteChanged();}
    private bool CanQuery()=>!Busy;
    private bool CanUpdate()=>!Busy&&CanManage;
    private bool CanTask()=>!Busy&&requestId.HasValue;
    public async Task EnsureInitializedAsync(){if(initialized)return;initialized=true;await QueryAsync();}
    [RelayCommand(CanExecute=nameof(CanQuery))]
    private async Task QueryAsync()
    {
        Busy=true;var current=++version;Clear();Status="正在读取年内累计净买入曲线…";
        using var pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=pending;pending.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var report=await workspace.SouthboundAnnualAsync(Years.Min(x=>x.Year),Years.Max(x=>x.Year),pending.Token);
            pending.Token.ThrowIfCancellationRequested();if(current!=version)return;
            Report=report;Caption=report.RetrievedAt is {} at?$"数据截至 {report.Through:yyyy-MM-dd} · 更新于 {at.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm} · 亿港元":"尚无累计净买入所需的已登记历史，可点击更新数据。";Rebuild();
        }
        catch(OperationCanceledException){if(current==version){Clear();Status="查询已取消或超时；后台采集继续运行。";}}
        catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or NotSupportedException)
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
        catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or NotSupportedException){TaskStatus=error.Message;}
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
    private void Clear(){Report=null;Series=[];HasValues=false;ResetZoom();Caption="每年一条折线，按相同月日对齐；单位：亿港元。";}
}

public partial class SouthboundYearSelection(int year,bool selected,Action changed):ObservableObject
{
    public int Year {get;}=year;
    public string Label=>Year+"年";
    [ObservableProperty]private bool isSelected=selected;
    partial void OnIsSelectedChanged(bool value)=>changed();
}
