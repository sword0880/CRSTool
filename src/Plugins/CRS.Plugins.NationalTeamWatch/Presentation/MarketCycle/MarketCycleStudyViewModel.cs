using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>周期页面仅展示后台冻结结果；手动查询，不计算价格、广度或评分，不触发采集。</summary>
public partial class MarketCycleStudyViewModel(IMarketCycleWorkspace workspace,CancellationToken lifetime):ObservableObject
{
    public MarketBreadthViewModel Breadth {get;}=new(workspace,lifetime);
    public ObservableCollection<string> Issues {get;}=[];
    public ObservableCollection<string> Evidence {get;}=[];
    public ObservableCollection<MarketCycleSourceReference> Sources {get;}=[];
    [ObservableProperty]private DateTime? asOf=CRS.Timing.BeijingClock.Now.Date.AddDays(-1);
    [ObservableProperty]private string cutoffText="";
    [ObservableProperty]private string status="选择已结束的统计日查询。缺少资料时保持无法判断。";
    [ObservableProperty]private MarketCycleStudyReport? report;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(QueryCommand))][NotifyCanExecuteChangedFor(nameof(CancelCommand))]private bool busy;
    public bool CanQuery=>!Busy;public bool CanCancel=>Busy;
    public string TopText=>Signal(Report?.Assessment.TopRiskLevel);
    public string BottomText=>Signal(Report?.Assessment.BottomCandidateLevel);
    public string RecoveryText=>Report?.Assessment.DataStatus!=MarketCycleDataStatus.ResearchPreview?"无法判断":Report.Assessment.RecoveryConfirmation?"符合修复候选条件":"未符合修复条件";
    public string CoverageText=>Report is null?"尚未查询":$"价格 {Report.Assessment.AcceptedPriceSessions}/250 日 · 两市成交 {Report.ActivityReadySessions}/21 日 · 广度 {Report.BreadthReadySessions}/6 日";
    public string Scopes=>Report is null?"价格、市场成交与广度分别验收；金额和成交量不能归因具名机构。":Report.PriceScope+"\n"+Report.ActivityScope+"\n"+Report.BreadthScope;
    private int version;private CancellationTokenSource? operation;
    partial void OnAsOfChanged(DateTime? value){Breadth.Date=value;Invalidate();}
    public void InvalidateForBreadth(){Invalidate();Status="广度资料操作已发起，请重新查询周期；已受理命令可在任务中心查看。";}
    partial void OnCutoffTextChanged(string value)=>Invalidate();
    partial void OnReportChanged(MarketCycleStudyReport? value)
    {OnPropertyChanged(nameof(TopText));OnPropertyChanged(nameof(BottomText));OnPropertyChanged(nameof(RecoveryText));OnPropertyChanged(nameof(CoverageText));OnPropertyChanged(nameof(Scopes));}
    private static string Signal(MarketCycleSignalLevel? value)=>value switch{MarketCycleSignalLevel.None=>"未触发候选条件",MarketCycleSignalLevel.Watch=>"观察",MarketCycleSignalLevel.Elevated=>"风险候选升高",MarketCycleSignalLevel.Candidate=>"底部区域候选",_=>"无法判断"};
    private void Invalidate(){version++;operation?.Cancel();Clear();Status="条件已改变，请重新查询。";}
    private void Clear(){Report=null;Issues.Clear();Evidence.Clear();Sources.Clear();}
    [RelayCommand(CanExecute=nameof(CanQuery))]private Task Query()=>ReadAsync();
    [RelayCommand(CanExecute=nameof(CanCancel))]private void Cancel()=>operation?.Cancel();
    public async Task ReadAsync()
    {
        var currentVersion=++version;operation?.Cancel();using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(EtfResearchTicket.MaximumWaitSeconds+10));
        Clear();Busy=true;Status="正在核对同一知识截止下的周期输入…";
        try
        {
            if(AsOf is not {} day)throw new InvalidDataException("请选择统计日期。");
            DateTimeOffset? cutoff=null;
            if(!string.IsNullOrWhiteSpace(CutoffText))
            {
                if(!DateTime.TryParseExact(CutoffText.Trim(),["yyyy-MM-dd HH:mm:ss","yyyy-MM-dd HH:mm:ss.FFFFFFF"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))throw new InvalidDataException("知识截止请输入北京时间 yyyy-MM-dd HH:mm:ss，或留空。");
                cutoff=new(DateTime.SpecifyKind(parsed,DateTimeKind.Unspecified),TimeSpan.FromHours(8));
            }
            var result=await workspace.MarketCycleStudyAsync(DateOnly.FromDateTime(day),cutoff,current.Token);
            if(currentVersion!=version||lifetime.IsCancellationRequested)return;
            Report=result;foreach(var issue in result.Assessment.DataIssues)Issues.Add(issue);
            foreach(var item in result.Assessment.TopEvidenceFamilies)Evidence.Add("顶部观察 · "+item);
            foreach(var item in result.Assessment.BottomEvidenceFamilies)Evidence.Add("底部观察 · "+item);
            if(Evidence.Count==0)Evidence.Add(result.Assessment.DataStatus==MarketCycleDataStatus.InsufficientData?"关键输入不足，全部周期信号已关闭。":"当前未触发证据族条件。");
            foreach(var source in result.Sources)Sources.Add(source);
            Status=$"{(result.Assessment.DataStatus==MarketCycleDataStatus.ResearchPreview?"研究预览，择时能力未验证":"资料不足，无法判断")} · 知识截止 {CRS.Timing.BeijingTime.Format(result.Input.EvaluationCutoff)}";
        }
        catch(OperationCanceledException){if(currentVersion==version){Clear();Status="本次查询已取消或超时；后台采集继续运行。";}}
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or NotSupportedException)
        {if(currentVersion==version){Clear();Status="周期查询不可用："+error.Message;}}
        finally{if(ReferenceEquals(operation,current)){operation=null;Busy=false;}}
    }
}
