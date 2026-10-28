using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>只展示后台已核披露与排除原因；不在界面计算份额变化或归因机构。</summary>
public partial class EtfHoldingHistoryViewModel(IEtfHoldingsWorkspace workspace,CancellationToken lifetime):ObservableObject
{
    public ObservableCollection<NamedEtfHoldingHistoryDisplayRow> Rows {get;}=[];
    public ObservableCollection<NamedEtfFamilyDisplayRow> Families {get;}=[];
    public ObservableCollection<string> SelectionDetails {get;}=[];
    [ObservableProperty]private EtfConfiguration? fund;
    [ObservableProperty]private DateTime? asOf=CRS.Timing.BeijingClock.Today.AddDays(-1);
    [ObservableProperty]private NamedEtfHoldingHistoryReport? report;
    [ObservableProperty]private string status="选择ETF及已结束统计日，手动读取已核披露。";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(QueryCommand)),NotifyCanExecuteChangedFor(nameof(CancelCommand))]private bool busy;
    public bool CanQuery=>!Busy&&Fund is {Active:true};public bool CanCancel=>Busy;
    private int version;private CancellationTokenSource? operation;
    partial void OnFundChanged(EtfConfiguration? value){Invalidate();QueryCommand.NotifyCanExecuteChanged();}
    partial void OnAsOfChanged(DateTime? value)=>Invalidate();
    private void Clear(){Report=null;Rows.Clear();Families.Clear();SelectionDetails.Clear();}
    private void Invalidate(){version++;operation?.Cancel();Clear();Status="条件已改变，请重新读取。";}
    [RelayCommand(CanExecute=nameof(CanCancel))]public void Cancel(){version++;operation?.Cancel();Clear();Status="已取消本次读取；后台采集继续运行。";}
    [RelayCommand(CanExecute=nameof(CanQuery))]private Task Query()=>ReadAsync();
    public async Task ReadAsync()
    {
        var stamp=++version;operation?.Cancel();using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));Clear();Busy=true;
        try
        {
            if(Fund is not {Active:true} selected||AsOf is not {} date)throw new InvalidDataException("请选择当前启用ETF及统计日。");
            Status="正在核对各期披露、原件和可比区间…";var key=new EtfKey(selected.Market,selected.Ticker);var through=DateOnly.FromDateTime(date);
            var next=await workspace.EtfHoldingHistoryAsync(key,through,current.Token);
            if(stamp!=version||lifetime.IsCancellationRequested)return;current.Token.ThrowIfCancellationRequested();
            if(next.Input.Key!=key||next.Input.AsOf!=through)throw new InvalidDataException("后台持仓历史与当前ETF或统计日不符。");
            Report=next;foreach(var row in next.Rows)Rows.Add(new(row));foreach(var family in next.Families)Families.Add(new(family));
            foreach(var item in next.Baseline.Issues)SelectionDetails.Add(item);
            foreach(var item in next.Baseline.Exclusions)SelectionDetails.Add(item.HoldingId+" · "+Reason(item.Reason));
            Status=$"{Rows.Count}条披露/缺口记录 · 基准{(next.Baseline.ReportAsOf is {} day?day.ToString("yyyy-MM-dd"):"未知")} · 仅{next.Baseline.Holdings.Count}个已核披露账户，完整持仓未证明 · 北京快照{CRS.Timing.BeijingTime.Format(next.Input.SnapshotCutoff)}";
        }
        catch(OperationCanceledException){if(stamp==version){Clear();Status="读取已取消或超时；后台采集继续运行。";}}
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or NotSupportedException)
        {if(stamp==version){Clear();Status="持仓历史不可用："+error.Message;}}
        finally{if(ReferenceEquals(operation,current)){operation=null;Busy=false;}}
    }
    private static string Reason(string code)=>code switch{"OlderReportPeriod"=>"较早报告期，不混入最新基准","KnownAfterCutoff"=>"公布或首次取得晚于快照","ReportAfterAsOf"=>"报告期晚于统计日","DuplicateSameFactEvidence"=>"同账户同份额的重复证据，不重复计数","LatestPeriodUnavailable"=>"最新期异常，整个最新基准关闭","InstitutionIdentityUnresolved"=>"主体不在已识别目录，不能归入国家队基准",_=>code};
}
public sealed record NamedEtfHoldingHistoryDisplayRow(NamedEtfHistoryRow Value)
{
    public string Status=>Value.State switch{"Increased"=>"报告期间增加","Decreased"=>"报告期间减少","Unchanged"=>"两期持平","NotDisclosed"=>"本期未列示，持仓未知","NotComparable"=>"可比性未核验","ConflictingDisclosures"=>"同一期披露冲突","AmbiguousLegalIdentity"=>"法人身份存在冲突","EvidenceUnavailable"=>"原件或独立持有待核验","PreviousEvidenceUnavailable"=>"前期证据不可用","IdentityChanged"=>"法人名称变更待核验",_=>"首次列示，前值未知"};
    public string References=>string.Join("\n",Value.EvidenceIds);
}
public sealed record NamedEtfFamilyDisplayRow(NamedEtfFamilyCoverage Value)
{
    public string StateText=>Value.State=="DisclosedSubsetOnly"?"仅有披露子集，覆盖率未知":"最新子集无已核披露，持仓未知";
}
