using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>只展示后台冻结研究，不计算金额、下载原件或修改份额和净值。</summary>
public partial class NavStudyViewModel(INavWorkspace workspace,IEtfResearchWorkspace etfs,CancellationToken lifetime):ObservableObject
{
    private bool initialized;
    private int generation;
    private CancellationTokenSource? operation;
    private EtfNavStudyReport? report;
    public ObservableCollection<EtfConfiguration> Funds {get;}=[];
    public ObservableCollection<NavStudyDisplayRow> Rows {get;}=[];
    public ObservableCollection<NavRevision> Revisions {get;}=[];
    public ObservableCollection<NavResearchSource> Sources {get;}=[];
    public bool CanQuery=>!Busy&&Selected is not null;
    public bool CanCopy=>report is not null&&!Busy;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(CanQuery)),NotifyCanExecuteChangedFor(nameof(QueryCommand))]
    private EtfConfiguration? selected;
    [ObservableProperty] private DateTime? from=CRS.Timing.BeijingClock.Now.Date.AddDays(-90);
    [ObservableProperty] private DateTime? through=CRS.Timing.BeijingClock.Now.Date.AddDays(-1);
    [ObservableProperty,NotifyPropertyChangedFor(nameof(CanQuery)),NotifyPropertyChangedFor(nameof(CanCopy)),NotifyCanExecuteChangedFor(nameof(QueryCommand)),NotifyCanExecuteChangedFor(nameof(CopyFrozenCommand))]
    private bool busy;
    [ObservableProperty] private string caption="选择已配置ETF，查询已登记份额与同日单位净值。";
    [ObservableProperty] private string status="当前已核对的NAV来源为159915；其他ETF按实际登记资料显示缺失。";
    [ObservableProperty] private string fingerprint="尚未生成研究快照";
    partial void OnSelectedChanged(EtfConfiguration? value)=>Invalidate();
    partial void OnFromChanged(DateTime? value)=>Invalidate();
    partial void OnThroughChanged(DateTime? value)=>Invalidate();
    public async Task EnsureInitializedAsync()
    {
        if(initialized)return;initialized=true;Busy=true;
        try
        {
            var values=await etfs.ConfigurationAsync(lifetime);
            Funds.Clear();foreach(var value in values)Funds.Add(value);
            Selected=Funds.FirstOrDefault(x=>x.Market=="SZSE"&&x.Ticker=="159915")??Funds.FirstOrDefault(x=>x.Active)??Funds.FirstOrDefault();
            if(Selected is null)Status="后台没有已登记ETF，请先在ETF配置中登记。";
        }
        catch(OperationCanceledException){Status="目录读取已取消。";initialized=false;}
        catch(Exception error) when(IsReadFailure(error)){Status="ETF目录读取失败："+error.Message;initialized=false;}
        finally{Busy=false;}
    }
    [RelayCommand(CanExecute=nameof(CanQuery))]
    private async Task QueryAsync()
    {
        var stamp=++generation;Clear();Busy=true;
        using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if(Selected is not {} selected||From is not {} from||Through is not {} through)
                throw new InvalidDataException("请选择ETF及完整日期区间。");
            var start=DateOnly.FromDateTime(from);var end=DateOnly.FromDateTime(through);var key=new EtfKey(selected.Market,selected.Ticker);
            if(start>end||end.DayNumber-start.DayNumber>366||end>DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.Date))
                throw new InvalidDataException("区间最多366日，不能包含未来日期。");
            var next=await workspace.NavStudyAsync(key,start,end,current.Token);
            if(stamp!=generation)return;
            if(next.FrozenInputs.Key!=key||next.FrozenInputs.From!=start||next.FrozenInputs.Through!=end)
                throw new InvalidDataException("后台研究与所选ETF或日期不符。");
            report=next;foreach(var row in next.Windows)Rows.Add(new(row));
            foreach(var row in next.FrozenInputs.NavRevisions??[])Revisions.Add(row);
            foreach(var source in next.FrozenInputs.Nav.Sources)Sources.Add(source);
            Fingerprint=next.Fingerprint;
            Caption=$"{selected.Ticker} · {selected.Name} · {start:yyyy-MM-dd} 至 {end:yyyy-MM-dd} · 知识截止 {next.FrozenInputs.KnowledgeCutoff.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}";
            Status=next.State=="NoRegisteredTradingDays"?"所选区间没有已登记交易日，无法形成研究窗口。"
                :$"已返回 {next.Windows.Count} 个研究窗口，其中 {next.Windows.Count(x=>x.EstimatedAmountYuan.HasValue)} 个金额可计算；其余保留缺失原因。不是国家队交易或退出信号。";
            OnPropertyChanged(nameof(CanCopy));CopyFrozenCommand.NotifyCanExecuteChanged();
        }
        catch(OperationCanceledException){if(stamp==generation){Clear();Status="查询已取消或超时；后台采集继续运行。";}}
        catch(Exception error) when(IsReadFailure(error)){if(stamp==generation){Clear();Status="NAV研究不可用："+error.Message;}}
        finally{if(ReferenceEquals(operation,current)){operation=null;Busy=false;}}
    }
    [RelayCommand] private void Cancel()=>operation?.Cancel();
    [RelayCommand(CanExecute=nameof(CanCopy))]
    private void CopyFrozen()
    {
        if(report is null)return;
        try{Clipboard.SetText(JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Status="已复制完整冻结研究JSON，含口径、修订身份和指纹；不是原件重新验收。";}
        catch(System.Runtime.InteropServices.ExternalException){Status="剪贴板暂不可用，请稍后重试。";}
    }
    private void Invalidate()
    {
        generation++;operation?.Cancel();operation=null;Busy=false;Clear();
        Status="查询条件已改变，请查询当前ETF与日期；旧结果已清除。";
    }
    private void Clear()
    {report=null;Rows.Clear();Revisions.Clear();Sources.Clear();Fingerprint="尚未生成研究快照";Caption="选择已配置ETF，查询已登记份额与同日单位净值。";OnPropertyChanged(nameof(CanCopy));CopyFrozenCommand.NotifyCanExecuteChanged();}
    private static bool IsReadFailure(Exception error)=>error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException or TimeoutException;
}

/// <summary>界面仅翻译后台状态和单位，不重新计算金融值。</summary>
public sealed record NavStudyDisplayRow(EtfNavStudyWindow Value)
{
    public string WindowText=>$"{Value.Window}个交易日";
    public string StatusText=>Value.State switch
    {
        "Complete"=>"完整可计算","NAV Unavailable"=>"缺少同日单位净值","IncompleteWarmup"=>"前置交易日不足",
        "UnverifiedCalendar"=>"交易日历未核验","ShareEvidenceUnavailable"=>"份额缺失、待审或时间证据不足",
        "UnverifiedComparability"=>"份额口径可比性未确认",_=>Value.State
    };
}
