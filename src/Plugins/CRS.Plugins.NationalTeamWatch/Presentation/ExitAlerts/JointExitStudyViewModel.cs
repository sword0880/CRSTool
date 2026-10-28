using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>展示同一后台快照的联合研究；目录延迟加载，条件改变立即作废旧结论。</summary>
public partial class JointExitStudyViewModel(IExitAlertWorkspace workspace,IEtfResearchWorkspace etfs,CancellationToken lifetime):ObservableObject
{
    public ObservableCollection<EtfConfiguration> Funds {get;}=[];
    public ObservableCollection<JointExitComponentRow> Components {get;}=[];
    public ObservableCollection<string> Support {get;}=[];
    public ObservableCollection<string> Counter {get;}=[];
    public ObservableCollection<string> Gaps {get;}=[];
    [ObservableProperty]private EtfConfiguration? selected;
    [ObservableProperty]private DateTime? asOf=CRS.Timing.BeijingClock.Now.Date.AddDays(-1);
    [ObservableProperty]private JointExitStudyReport? report;
    [ObservableProperty]private string status="选择配置池中的ETF与已结束统计日，手动查询。";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(QueryCommand)),NotifyCanExecuteChangedFor(nameof(CancelCommand))]private bool busy;
    public bool CanQuery=>!Busy&&Selected is {Active:true};public bool CanCancel=>Busy;
    private bool initialized;private int generation;private CancellationTokenSource? operation;
    partial void OnSelectedChanged(EtfConfiguration? value){Invalidate();QueryCommand.NotifyCanExecuteChanged();}
    partial void OnAsOfChanged(DateTime? value)=>Invalidate();
    private void Clear(){Report=null;Components.Clear();Support.Clear();Counter.Clear();Gaps.Clear();}
    private void Invalidate(){generation++;operation?.Cancel();Clear();Status="条件已改变，请重新查询。";}
    public async Task EnsureInitializedAsync()
    {
        if(initialized||Busy)return;Busy=true;using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var values=await etfs.ConfigurationAsync(current.Token);current.Token.ThrowIfCancellationRequested();
            Funds.Clear();foreach(var value in values.Where(x=>x.Active))Funds.Add(value);
            Selected=Funds.FirstOrDefault();initialized=true;Status=Selected is null?"当前没有启用ETF，请先在ETF配置中登记。":"目录已读取，请选择ETF后查询。";
        }
        catch(OperationCanceledException){Status="目录读取已取消或超时。";}
        catch(Exception error) when(ReadFailure(error)){Status="ETF目录不可用："+error.Message;}
        finally{if(ReferenceEquals(operation,current)){operation=null;Busy=false;}}
    }
    [RelayCommand(CanExecute=nameof(CanQuery))]private Task Query()=>ReadAsync();
    [RelayCommand(CanExecute=nameof(CanCancel))]private void Cancel()=>operation?.Cancel();
    public async Task ReadAsync()
    {
        var stamp=++generation;operation?.Cancel();using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));
        Clear();Busy=true;Status="正在核对五项证据与时点…";
        try
        {
            if(Selected is not {Active:true} fund||AsOf is not {} day)throw new InvalidDataException("请选择启用ETF和统计日。");
            var key=new EtfKey(fund.Market,fund.Ticker);var date=DateOnly.FromDateTime(day);
            var next=await workspace.JointExitStudyAsync(key,date,current.Token);
            if(stamp!=generation||lifetime.IsCancellationRequested)return;current.Token.ThrowIfCancellationRequested();
            if(next.Input.Configuration.Market!=key.Market||next.Input.Configuration.Ticker!=key.Ticker||next.Input.AsOfDate!=date)throw new InvalidDataException("后台结果与当前ETF或统计日不符。");
            Report=next;foreach(var row in next.Components)Components.Add(new(row));
            foreach(var item in next.SupportingEvidence)Support.Add(item);foreach(var item in next.CounterEvidence)Counter.Add(item);foreach(var item in next.BlockingReasons)Gaps.Add(item);
            Status=next.StateText+" · 北京快照 "+CRS.Timing.BeijingTime.Format(next.Input.SnapshotCutoff)+" · 仅研究，通知关闭";
        }
        catch(OperationCanceledException){if(stamp==generation){Clear();Status="本次查询已取消或超时；后台采集继续运行。";}}
        catch(Exception error) when(ReadFailure(error)){if(stamp==generation){Clear();Status="联合观察不可用："+error.Message;}}
        finally{if(ReferenceEquals(operation,current)){operation=null;Busy=false;}}
    }
    private static bool ReadFailure(Exception error)=>error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or NotSupportedException;
}
public sealed record JointExitComponentRow(JointExitComponent Value)
{
    public string Readiness=>Value.Ready?"满足研究输入门槛":"资料待补齐";
    public string DateText=>Value.DataDate?.ToString("yyyy-MM-dd")??"尚无有效日期";
    public string References=>string.Join("\n",Value.EvidenceReferences);
}
