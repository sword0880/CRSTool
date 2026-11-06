using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;
using CRS.Maintenance;
namespace CRS.DesktopClient.ViewModels;

public sealed record CleanupEntryView(DataCleanupEntry Entry,string State="Ready")
{
    public string Name=>Entry.Name;
    public string Disposition=>Entry.Selected?State switch{"Ready"=>"将清理","Completed"=>"已清理","Running"=>"清理范围",_=>"待核对"}:Entry.Eligible?"未选中":"保留";
    public string Size=>Entry.Bytes is {} bytes?DataCleanupViewModel.FormatBytes(bytes):"未统计";
    public string Detail=>Entry.RelativePath+"\n"+Entry.Reason;
}
/// <summary>只展示后台判定结果；清理不参与保存设置，操作编号用于断线后的结果查询。</summary>
public sealed partial class DataCleanupViewModel(IDataCleanupGateway? gateway):ObservableObject
{
    private DataCleanupStatus? preview;private DataCleanupStatus? lastStatus;private Guid? pendingOperationId;private CancellationTokenSource? waiting;
    private int waitingGeneration;
    public bool Available=>gateway?.CanExecute==true;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(ScanCommand)),NotifyCanExecuteChangedFor(nameof(ExecuteCommand)),NotifyCanExecuteChangedFor(nameof(CheckResultCommand)),NotifyCanExecuteChangedFor(nameof(StopWaitingCommand)),NotifyPropertyChangedFor(nameof(CanEdit))]private bool busy;
    [ObservableProperty]private int logRetentionDays=14;
    [ObservableProperty]private int backupsToKeep=1;
    [ObservableProperty]private bool includeBackups=true;
    [ObservableProperty]private string message="先扫描用途与大小，再按预览清理。扫描不会修改后台资料。";
    [ObservableProperty]private string summary="尚未扫描";
    [ObservableProperty]private string elapsedText="";
    [ObservableProperty]private string currentItem="";
    [ObservableProperty]private string verificationText="";
    [ObservableProperty]private bool progressIndeterminate=true;
    [ObservableProperty]private double progressPercent;
    public string AccessMessage=>Available?"资料范围由后台服务确定，通常为 C:\\ProgramData\\CRS\\Background。":"需要管理员权限及配套后台服务；当前不能执行清理。";
    public bool CanEdit=>Available&&!Busy&&pendingOperationId is null;
    public ObservableCollection<CleanupEntryView> Entries{get;}=[];
    partial void OnLogRetentionDaysChanged(int value)=>Invalidate();
    partial void OnBackupsToKeepChanged(int value)=>Invalidate();
    partial void OnIncludeBackupsChanged(bool value)=>Invalidate();
    private void Invalidate(){preview=null;Entries.Clear();Summary="选项已更改，请重新扫描";ExecuteCommand.NotifyCanExecuteChanged();}
    private bool CanScan()=>CanEdit;
    private bool CanExecute()=>CanEdit&&preview is{State:"Ready",CanExecute:true};
    private bool CanCheckResult()=>Available&&!Busy&&pendingOperationId is not null;
    [RelayCommand(CanExecute=nameof(CanScan))]private async Task ScanAsync()
    {
        preview=null;Entries.Clear();
        await RunAsync(progress=>gateway!.PreviewAsync(new(LogRetentionDays,BackupsToKeep,IncludeBackups),progress,waiting!.Token),false,"正在扫描");
    }
    [RelayCommand(CanExecute=nameof(CanExecute))]private async Task ExecuteAsync()
    {
        var id=preview!.OperationId;pendingOperationId=Guid.NewGuid();
        await RunAsync(progress=>gateway!.ExecuteAsync(id,pendingOperationId.Value,progress,waiting!.Token),true,"正在清理");
    }
    [RelayCommand(CanExecute=nameof(CanCheckResult))]private async Task CheckResultAsync()
        =>await RunAsync(progress=>gateway!.StatusAsync(pendingOperationId!.Value,progress,waiting!.Token),lastStatus?.State!="Scanning","正在查询进度");
    private async Task RunAsync(Func<IProgress<DataCleanupStatus>,Task<DataCleanupStatus>> action,bool mayHaveDeleted,string startingSummary)
    {
        var generation=++waitingGeneration;Busy=true;waiting?.Dispose();waiting=new();
        Summary=startingSummary;Message="正在连接后台，请稍候。";CurrentItem="";VerificationText="";ProgressIndeterminate=true;ProgressPercent=0;
        var elapsed=Stopwatch.StartNew();ElapsedText="已用时 00:00";
        var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(1)};
        timer.Tick+=(_,_)=>ElapsedText="已用时 "+Elapsed(elapsed.Elapsed);timer.Start();
        try
        {
            var progress=new Progress<DataCleanupStatus>(status=>{if(Busy&&generation==waitingGeneration)Apply(status);});
            var result=await action(progress);++waitingGeneration;Apply(result);
        }
        catch(OperationCanceledException){Message=mayHaveDeleted?"已停止等待；后台清理可能继续，请查询结果。":"已停止等待扫描；未提交清理，可查询扫描进度。";}
        catch(Exception error){Message=(mayHaveDeleted?"清理结果尚未确认；请点击查询结果，避免重复提交。":"扫描未完成，未提交清理。")+" "+error.Message;}
        finally{++waitingGeneration;timer.Stop();ElapsedText="已用时 "+Elapsed(elapsed.Elapsed);Busy=false;NotifyCommands();}
    }
    private static string Elapsed(TimeSpan elapsed)=>elapsed.TotalHours>=1?elapsed.ToString(@"hh\:mm\:ss"):elapsed.ToString(@"mm\:ss");
    private void Apply(DataCleanupStatus status)
    {
        lastStatus=status;Message=status.Message;Entries.Clear();
        foreach(var entry in status.Entries.OrderByDescending(x=>x.Selected).ThenByDescending(x=>x.Eligible))Entries.Add(new(entry,status.State));
        preview=status.State=="Ready"?status:null;
        Summary=status.State switch
        {
            "Ready"=>"本次将清理 "+FormatBytes(status.Entries.Where(x=>x.Selected).Sum(x=>x.Bytes??0))+" · "+status.Entries.Count(x=>x.Selected)+" 项",
            "Scanning"=>status.Progress is {} progress?$"正在扫描 · 已扫描 {progress.ScannedFiles:N0} 个文件 / {FormatBytes(progress.ScannedBytes)}":"正在扫描",
            _=>status.CountsComplete?$"{StateText(status.State)} · 已清理 {FormatBytes(status.DeletedBytes)} / {status.DeletedFiles:N0} 个文件":$"{StateText(status.State)} · 删除数量未完整记录，请重新扫描"
        };
        ApplyProgress(status);
        if(status.State is "Running" or "Scanning")pendingOperationId=status.OperationId;
        else if(status.OperationId==pendingOperationId)pendingOperationId=null;
        NotifyCommands();
    }
    private void ApplyProgress(DataCleanupStatus status)
    {
        var active=status.State is "Scanning" or "Running";var progress=status.Progress;
        CurrentItem=active&&progress?.CurrentItem is {} path?"当前文件或目录："+path:"";
        VerificationText=active&&progress is{VerifiedBytes:>0}?"校验已读取 "+FormatBytes(progress.VerifiedBytes):"";
        var selected=status.Entries.Where(x=>x.Selected).ToArray();var totalFiles=selected.Sum(x=>(long)(x.Files??0));
        ProgressIndeterminate=!active||status.State=="Scanning"||progress?.Phase!="Deleting"||totalFiles==0||selected.Any(x=>x.Files is null);
        ProgressPercent=totalFiles>0?Math.Clamp(100d*status.DeletedFiles/totalFiles,0,100):0;
        if(status.State=="Running"&&totalFiles>0)Summary+=$" / 共 {totalFiles:N0} 个文件";
    }
    private void NotifyCommands(){OnPropertyChanged(nameof(CanEdit));ScanCommand.NotifyCanExecuteChanged();ExecuteCommand.NotifyCanExecuteChanged();CheckResultCommand.NotifyCanExecuteChanged();StopWaitingCommand.NotifyCanExecuteChanged();}
    private bool CanStopWaiting()=>Busy&&pendingOperationId is not null;
    [RelayCommand(CanExecute=nameof(CanStopWaiting))]public void StopWaiting()=>waiting?.Cancel();
    private static string StateText(string state)=>state switch{"Scanning"=>"正在扫描","Running"=>"正在清理","Completed"=>"清理完成","Interrupted"=>"未全部完成","Unavailable"=>"扫描不可用","Expired"=>"预览已过期",_=>"结果未知"};
    internal static string FormatBytes(long bytes)=>bytes>=1024L*1024*1024?$"{bytes/(1024d*1024*1024):N2} GB":bytes>=1024*1024?$"{bytes/(1024d*1024):N1} MB":$"{bytes/1024d:N1} KB";
}
