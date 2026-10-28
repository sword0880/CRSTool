using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>只查询系统登记及审核时点；不计算行情、不写库、不停止后台采集。</summary>
public partial class MarketActivityKnowledgeViewModel(IMarketCycleWorkspace workspace,CancellationToken lifetime):ObservableObject
{
    public ObservableCollection<MarketActivityKnowledgeRevision> Rows {get;}=[];
    public MarketActivityKnowledgeReport? Report {get;private set;}
    [ObservableProperty]private DateTime? from=CRS.Timing.BeijingClock.Now.Date;
    [ObservableProperty]private DateTime? through=CRS.Timing.BeijingClock.Now.Date;
    [ObservableProperty]private string cutoffText="";
    [ObservableProperty]private string status="按实际登记和审核时间查询单市场原件；不将今天知道的结果回填为过去已经可知。";
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(QueryCommand))][NotifyCanExecuteChangedFor(nameof(CancelCommand))]private bool busy;
    private int version;private CancellationTokenSource? operation;
    public bool CanQuery=>!Busy;public bool CanCancel=>Busy;
    partial void OnFromChanged(DateTime? value)=>Invalidate();
    partial void OnThroughChanged(DateTime? value)=>Invalidate();
    partial void OnCutoffTextChanged(string value)=>Invalidate();
    private void Invalidate(){version++;operation?.Cancel();Clear();Status="条件已改变，请重新查询。知识截止留空使用本次后台查询时刻。";}
    private void Clear(){Rows.Clear();Report=null;OnPropertyChanged(nameof(Report));}
    [RelayCommand(CanExecute=nameof(CanQuery))]private Task Query()=>ReadAsync();
    [RelayCommand(CanExecute=nameof(CanCancel))]private void Cancel()=>operation?.Cancel();
    public async Task ReadAsync()
    {
        var currentVersion=++version;operation?.Cancel();using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));
        Clear();Busy=true;Status="正在查询成交知识时点…";
        try
        {
            if(From is not {} begin||Through is not {} end)throw new InvalidDataException("请选择完整的统计日期区间。");
            DateTimeOffset? cutoff=null;
            if(!string.IsNullOrWhiteSpace(CutoffText))
            {
                if(!DateTime.TryParseExact(CutoffText.Trim(),["yyyy-MM-dd HH:mm:ss","yyyy-MM-dd HH:mm:ss.FFFFFFF"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))
                    throw new InvalidDataException("知识截止请输入北京时间 yyyy-MM-dd HH:mm:ss，或留空。");
                cutoff=new DateTimeOffset(DateTime.SpecifyKind(parsed,DateTimeKind.Unspecified),TimeSpan.FromHours(8));
            }
            var report=await workspace.MarketActivityKnowledgeAsync(DateOnly.FromDateTime(begin),DateOnly.FromDateTime(end),cutoff,current.Token);
            if(currentVersion!=version||lifetime.IsCancellationRequested)return;
            Report=report;OnPropertyChanged(nameof(Report));foreach(var row in report.Revisions)Rows.Add(row);
            Status=$"知识截止 {CRS.Timing.BeijingTime.Format(report.Cutoff)} · 可见原件 {report.Revisions.Count} 条。旧资料从迁移登记时刻起可知；无记录不代表零成交。";
        }
        catch(OperationCanceledException){if(currentVersion==version){Clear();Status="本次查询已取消或超时；后台采集继续运行。";}}
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or NotSupportedException)
        {if(currentVersion==version){Clear();Status="成交知识查询不可用："+error.Message;}}
        finally{if(ReferenceEquals(operation,current)){operation=null;Busy=false;}}
    }
}
