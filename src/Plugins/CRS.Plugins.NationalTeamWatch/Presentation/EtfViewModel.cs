using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
/// <summary>仅管理界面状态和交互命令，数据判断与存储交由应用用例。</summary>
public partial class EtfViewModel(IEtfWorkspace workspace, CancellationToken lifetime) : ObservableObject
{
    public bool CanManage=>workspace.CanManage;
    public ObservableCollection<EtfSummary> Rows { get; }=[];
    public ObservableCollection<EvidenceRow> Evidence { get; }=[];
    public ObservableCollection<CoverageDay> CoverageDays { get; }=[];
    [ObservableProperty] private DateTime? coverageFrom=MarketToday.AddYears(-1);
    [ObservableProperty] private DateTime? coverageTo=MarketToday;
    [ObservableProperty] private string coverageStatus="选择ETF和日期区间，查询已登记交易日历口径的覆盖率。";
    [ObservableProperty] private EtfSummary? selected;
    [ObservableProperty] private EvidenceRow? selectedEvidence;
    [ObservableProperty] private string reviewer="";
    [ObservableProperty] private bool confirmedComparable;
    [ObservableProperty] private string status="正在加载研究数据；NAV金额不可用。";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private DateTime? syncFrom=MarketToday.AddDays(-35);
    [ObservableProperty] private DateTime? syncTo=MarketToday;
    [ObservableProperty] private ISeries[] series=[];
    [ObservableProperty] private LiveChartsCore.SkiaSharpView.Axis[] xAxes=[];
    [ObservableProperty] private string market="SSE";
    [ObservableProperty] private string ticker="";
    [ObservableProperty] private string securityName="";
    [ObservableProperty] private string indexGroup="";
    [ObservableProperty] private string reason="";
    private CancellationTokenSource? operation;
    private int selectionVersion;
    // 研究日期以北京时间为准，不依赖电脑的本地时区。
    private static DateTime MarketToday=>TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").Date;
    partial void OnSelectedChanged(EtfSummary? value)
    {
        // 切换证券后立即清掉旧图表和覆盖率，避免把旧证券的数据展示到新选择上。
        Series=[];XAxes=[];InvalidateCoverage();
    }
    partial void OnCoverageFromChanged(DateTime? value)=>InvalidateCoverage();
    partial void OnCoverageToChanged(DateTime? value)=>InvalidateCoverage();
    private void InvalidateCoverage()
    {
        // 输入在后台查询期间也可能改变，用版本号拒绝旧请求回写到新选择。
        selectionVersion++;CoverageDays.Clear();CoverageStatus="选择ETF和日期区间后查询；刷新数据后需要重新查询。";
    }
    private async Task ExecuteAsync(Func<CancellationToken,Task> work,CancellationToken cancellationToken=default)
    {
        if(Busy || lifetime.IsCancellationRequested) return;
        // 自动任务同时响应宿主调度取消和插件会话取消，手动命令继续支持界面取消。
        Busy=true; operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime,cancellationToken);
        try {await work(operation.Token);}
        catch(OperationCanceledException) {Status="已停止等待；服务已接受的任务可能继续运行，请在任务管理中查询或取消。";}
        catch(Exception error) {Status=error is InvalidDataException?error.Message:"操作失败；类别："+error.GetType().Name;}
        finally {operation.Dispose();operation=null;Busy=false;}
    }
    public async Task RefreshAsync(CancellationToken token)
    {
        var rows=await workspace.OverviewAsync(token);var evidence=await workspace.EvidenceAsync(token); token.ThrowIfCancellationRequested();
        Selected=null;SelectedEvidence=null;Series=[];XAxes=[];InvalidateCoverage();CoverageStatus="本地数据已刷新，请重新选择ETF查询覆盖率。";
        Rows.Clear();foreach(var row in rows) Rows.Add(row); Evidence.Clear();foreach(var row in evidence) Evidence.Add(row);
        Status=$"当前验证池 {Rows.Count}/20 · {workspace.NetworkStatus}";
    }
    /// <summary>后台采集结束后的展示刷新不覆盖用户正在进行的交互。</summary>
    public Task RefreshFromScheduleAsync(CancellationToken token)=>Busy?Task.CompletedTask:RefreshAsync(token);
    [RelayCommand] private Task Refresh()=>ExecuteAsync(RefreshAsync);
    /// <summary>区间和命令属于界面交互；下载、解析与日历登记由后台用例执行。</summary>
    [RelayCommand] private Task SyncHistory()=>ExecuteAsync(async token=>
    {
        if(SyncFrom is null || SyncTo is null)throw new InvalidDataException("请设置采集日期区间。");
        await workspace.SyncAsync(DateOnly.FromDateTime(SyncFrom.Value),DateOnly.FromDateTime(SyncTo.Value),token);
        await RefreshAsync(token);
    });
    public Task SyncLatestAsync(CancellationToken cancellationToken)=>ExecuteAsync(async token=>
    {
        var today=DateOnly.FromDateTime(MarketToday);
        try{await workspace.SyncAsync(today.AddDays(-7),today,token);}
        finally{if(!token.IsCancellationRequested)await RefreshAsync(token);}
    },cancellationToken);
    [RelayCommand] private void Cancel()=>operation?.Cancel();
    [RelayCommand] private Task ShowHistory()=>ExecuteAsync(async token=>
    {
        if(Selected is null) return;
        var requestedVersion=selectionVersion;
        var points=await workspace.PointsAsync(new(Selected.Market,Selected.Ticker),token);token.ThrowIfCancellationRequested();
        if(requestedVersion!=selectionVersion)return;
        Series=[new LineSeries<decimal> {Name="ETF全市场总份额（份）",Values=points.Select(x=>x.Shares).ToArray(),GeometrySize=3,Fill=null,LineSmoothness=0,
            Stroke=new LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint(SkiaSharp.SKColors.DodgerBlue){StrokeThickness=2},GeometryFill=new LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint(SkiaSharp.SKColors.DodgerBlue)}];
        XAxes=[new() {Labels=points.Select(x=>x.Date.ToString("yyyy-MM-dd")).ToArray()}];
    });
    /// <summary>界面只展示后台覆盖判定；250日是一期目标，不能把尚未登记的日历当成已覆盖。</summary>
    [RelayCommand] private Task ShowCoverage()=>ExecuteAsync(async token=>
    {
        if(Selected is null || CoverageFrom is null || CoverageTo is null)throw new InvalidDataException("请先选择ETF和完整日期区间。");
        var requestedVersion=selectionVersion;
        var report=await workspace.CoverageAsync(new(Selected.Market,Selected.Ticker),DateOnly.FromDateTime(CoverageFrom.Value),DateOnly.FromDateTime(CoverageTo.Value),token);
        token.ThrowIfCancellationRequested();if(requestedVersion!=selectionVersion)return;
        CoverageDays.Clear();foreach(var day in report.Days)CoverageDays.Add(day);
        CoverageStatus=$"{report.Market}:{report.Ticker} · 已登记交易日 {report.RegisteredTradingDays} · 有效 {report.AcceptedDays} · 缺失 {report.MissingDays} · 待审核 {report.PendingDays} · 证据异常 {report.CriticalDays} · 拒绝 {report.RejectedDays} · 覆盖率 {(report.CoveragePercent is null?"不可判定":report.CoveragePercent.Value+"%")} · {report.StatusText}。一期目标250个有效交易日；本比例只针对所选区间内已登记的日历。";
    });
    [RelayCommand] private Task ExportOriginal()=>ExportEvidence(true);
    [RelayCommand] private Task ExportNormalized()=>ExportEvidence(false);
    /// <summary>选择导出目的地属于交互，读取原件、验证和写入仍交给应用用例。</summary>
    private Task ExportEvidence(bool original)=>ExecuteAsync(async token=>
    {
        if(SelectedEvidence is null)throw new InvalidDataException("请先选择一条证据修订。");
        var dialog=new Microsoft.Win32.SaveFileDialog {FileName=SelectedEvidence.Ticker+"-"+SelectedEvidence.TradeDate+(original?"-original.bin":"-normalized.csv"),Filter=original?"原始证据字节|*.bin|所有文件|*.*":"标准化CSV|*.csv",OverwritePrompt=true};
        if(dialog.ShowDialog()!=true)return;
        await workspace.ExportEvidenceAsync(SelectedEvidence.RevisionId,original,dialog.FileName,token);Status="证据已核验并导出；原始文件内容保持不变。";
    });
    [RelayCommand] private Task Approve()=>Review(true);
    [RelayCommand] private Task Reject()=>Review(false);
    private Task Review(bool accept)=>ExecuteAsync(async token=>
    { if(SelectedEvidence is null) return; await workspace.ReviewAsync(SelectedEvidence.RevisionId,accept,Reviewer,ConfirmedComparable,token);await RefreshAsync(token); });
    [RelayCommand] private Task AddToPool()=>Membership(true);
    [RelayCommand] private Task RemoveFromPool()=>Membership(false);
    private Task Membership(bool active)=>ExecuteAsync(async token=>
    {await workspace.MembershipAsync(new(Market,Ticker),SecurityName,IndexGroup,active,Reason,token);await RefreshAsync(token);});
}
