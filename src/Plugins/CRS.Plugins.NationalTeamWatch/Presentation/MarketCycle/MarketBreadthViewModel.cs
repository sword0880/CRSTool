using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>只展示后台档案及回执；文件字节处理在客户端公共机制，广度计数和审核在后台。</summary>
public partial class MarketBreadthViewModel(IMarketCycleWorkspace workspace,CancellationToken lifetime):ObservableObject
{
    public ObservableCollection<MarketBreadthRevision> Rows {get;}=[];
    public event Action? Changed;
    private MarketBreadthHistory? snapshot;private CancellationTokenSource? operation;private int version,generation;private bool applyingReceipt;
    [ObservableProperty]private DateTime? date=CRS.Timing.BeijingClock.Now.Date.AddDays(-1);
    [ObservableProperty]private string status="读取当天档案；登记不会自动核验。原件、当日名单和价格口径需分别核对。";
    [ObservableProperty]private MarketBreadthRevision? selected;
    [ObservableProperty]private string originalPath="";
    [ObservableProperty]private string normalizedPath="";
    [ObservableProperty]private string sourceUrl="";
    [ObservableProperty]private string retrievedText="";
    [ObservableProperty]private string submitter="";
    [ObservableProperty]private string submissionReason="";
    [ObservableProperty]private string reviewer="";
    [ObservableProperty]private string reviewReason="";
    [ObservableProperty]private bool originalConfirmed;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(QueryCommand))][NotifyCanExecuteChangedFor(nameof(SubmitCommand))][NotifyCanExecuteChangedFor(nameof(ConfirmCommand))][NotifyCanExecuteChangedFor(nameof(AcceptCommand))][NotifyCanExecuteChangedFor(nameof(RejectCommand))][NotifyCanExecuteChangedFor(nameof(CancelCommand))]private bool busy;
    public bool CanQuery=>!Busy;public bool CanEdit=>workspace.CanManage&&!Busy;public bool CanCancel=>Busy;
    partial void OnBusyChanged(bool value)=>OnPropertyChanged(nameof(CanEdit));
    partial void OnDateChanged(DateTime? value){version++;if(!applyingReceipt)operation?.Cancel();Clear();Status="日期已改变，请重新读取档案。";}
    partial void OnSelectedChanged(MarketBreadthRevision? value){OriginalConfirmed=false;ReviewReason="";}
    partial void OnReviewerChanged(string value)=>OriginalConfirmed=false;
    partial void OnReviewReasonChanged(string value)=>OriginalConfirmed=false;
    private void Clear(){Rows.Clear();Selected=null;snapshot=null;}
    [RelayCommand(CanExecute=nameof(CanQuery))]private Task Query()=>Run(ReadAsync,30);
    [RelayCommand(CanExecute=nameof(CanCancel))]private void Cancel()=>operation?.Cancel();
    private async Task ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(Date is not {} day)throw new InvalidDataException("请选择统计日期。");var current=++version;Clear();
        var result=await workspace.MarketBreadthHistoryAsync(DateOnly.FromDateTime(day),token);if(current!=version||lifetime.IsCancellationRequested)return;
        snapshot=result;foreach(var row in result.Rows)Rows.Add(row);
        Status=$"档案 {Rows.Count} 份 · 知识截止 {CRS.Timing.BeijingTime.Format(result.Cutoff)}。待核验或同日竞争版本会关闭周期信号。";
    }
    [RelayCommand(CanExecute=nameof(CanEdit))]private Task Submit()=>Run(async token=>
    {
        RequireManage();
        if(string.IsNullOrWhiteSpace(OriginalPath)||string.IsNullOrWhiteSpace(NormalizedPath))throw new InvalidDataException("请选择原件和标准化档案。");
        if(!Uri.TryCreate(SourceUrl.Trim(),UriKind.Absolute,out var source)||source.Scheme!="https"||string.IsNullOrWhiteSpace(Submitter)||string.IsNullOrWhiteSpace(SubmissionReason))throw new InvalidDataException("请填写HTTPS来源、登记人和登记说明。");
        if(!DateTime.TryParseExact(RetrievedText.Trim(),["yyyy-MM-dd HH:mm:ss","yyyy-MM-dd HH:mm:ss.FFFFFFF"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var acquired))throw new InvalidDataException("取得时间请输入北京时间 yyyy-MM-dd HH:mm:ss，按实际取得记录填写。");
        Changed?.Invoke();Status="正在提交文件；中断后可重新选择原文件续传。";
        var currentGeneration=generation;var progress=new Progress<(int Received,int Total)>(x=>{if(Busy&&generation==currentGeneration&&!token.IsCancellationRequested)Status=$"文件提交 {x.Received:N0}/{x.Total:N0} 字节；只有后台回执确认后才完成登记。";});
        var receipt=await workspace.SubmitMarketBreadthAsync(OriginalPath,NormalizedPath,SourceUrl.Trim(),new(DateTime.SpecifyKind(acquired,DateTimeKind.Unspecified),TimeSpan.FromHours(8)),Submitter.Trim(),SubmissionReason.Trim(),token,progress);
        await ShowReceipt(receipt,token);
    },300);
    [RelayCommand(CanExecute=nameof(CanEdit))]private Task Confirm()=>Run(async token=>
    {RequireManage();var receipt=await workspace.ConfirmMarketBreadthSubmissionAsync(token);Changed?.Invoke();await ShowReceipt(receipt,token);},60);
    private async Task ShowReceipt(MarketBreadthSubmissionReceipt receipt,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();applyingReceipt=true;try{Date=receipt.Date.ToDateTime(TimeOnly.MinValue);}finally{applyingReceipt=false;}await ReadAsync(token);
        Status=$"后台已确认登记 · 请求 {receipt.RequestId:D}。审核状态以档案列表为准，请重新查询周期评估。";
    }
    [RelayCommand(CanExecute=nameof(CanEdit))]private Task Accept()=>Review(true);
    [RelayCommand(CanExecute=nameof(CanEdit))]private Task Reject()=>Review(false);
    private Task Review(bool accept)=>Run(async token=>
    {
        RequireManage();
        if(snapshot is null||Selected is not {} row)throw new InvalidDataException("请先读取并选择当天档案。");
        if(accept&&!OriginalConfirmed)throw new InvalidDataException("请明确确认已核对原件、当日名单及价格口径。");
        var request=new MarketBreadthReview(row.Id,row.Date,snapshot.StateFingerprint,accept,Reviewer.Trim(),ReviewReason.Trim(),OriginalConfirmed);
        Changed?.Invoke();await workspace.ReviewMarketBreadthAsync(request,token);OriginalConfirmed=false;await ReadAsync(token);
        Status="后台审核已完成；竞争版本的处理已留痕。请重新查询周期评估。";
    },60);
    public Task ExportAsync(bool original,string destination)=>Run(async token=>
    {
        if(Selected is not {} row)throw new InvalidDataException("请先选择档案。");await workspace.ExportMarketBreadthEvidenceAsync(row.Id,original,destination,token);Status="文件已完整核对摘要并导出。";
    },60);
    private void RequireManage(){if(!workspace.CanManage)throw new UnauthorizedAccessException("登记及核验需要管理员权限，当前可以读取和导出档案。");}
    private async Task Run(Func<CancellationToken,Task> work,int seconds)
    {
        if(Busy)return;generation++;using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime);operation=current;current.CancelAfter(TimeSpan.FromSeconds(seconds));Busy=true;
        try{await work(current.Token);}
        catch(OperationCanceledException){Status="本次等待已取消或超时；已受理命令继续保留，请回读登记结果或查看任务中心。";}
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or NotSupportedException)
        {Status="档案操作未确认："+error.Message;}
        finally{if(ReferenceEquals(operation,current)){operation=null;Busy=false;}}
    }
}
