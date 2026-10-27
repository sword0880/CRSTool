using System.Collections.ObjectModel;
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
    private DataCleanupStatus? preview;private Guid? executionId;private CancellationTokenSource? waiting;
    public bool Available=>gateway?.CanExecute==true;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(ScanCommand)),NotifyCanExecuteChangedFor(nameof(ExecuteCommand)),NotifyCanExecuteChangedFor(nameof(CheckResultCommand)),NotifyPropertyChangedFor(nameof(CanEdit))]private bool busy;
    [ObservableProperty]private int logRetentionDays=14;
    [ObservableProperty]private int backupsToKeep=2;
    [ObservableProperty]private bool includeBackups;
    [ObservableProperty]private string message="先扫描用途与大小，再按预览清理。扫描不会修改后台资料。";
    [ObservableProperty]private string summary="尚未扫描";
    public string AccessMessage=>Available?"资料范围由后台服务确定，通常为 C:\\ProgramData\\CRS\\Background。":"需要管理员权限及配套后台服务；当前不能执行清理。";
    public bool CanEdit=>Available&&!Busy&&executionId is null;
    public ObservableCollection<CleanupEntryView> Entries{get;}=[];
    partial void OnLogRetentionDaysChanged(int value)=>Invalidate();
    partial void OnBackupsToKeepChanged(int value)=>Invalidate();
    partial void OnIncludeBackupsChanged(bool value)=>Invalidate();
    private void Invalidate(){preview=null;Entries.Clear();Summary="选项已更改，请重新扫描";ExecuteCommand.NotifyCanExecuteChanged();}
    private bool CanScan()=>CanEdit;
    private bool CanExecute()=>CanEdit&&preview is{State:"Ready",CanExecute:true};
    private bool CanCheckResult()=>Available&&!Busy&&executionId is not null;
    [RelayCommand(CanExecute=nameof(CanScan))]private async Task ScanAsync()=>await RunAsync(async progress=>
    {preview=null;Apply(await gateway!.PreviewAsync(new(LogRetentionDays,BackupsToKeep,IncludeBackups),progress,waiting!.Token));},false);
    [RelayCommand(CanExecute=nameof(CanExecute))]private async Task ExecuteAsync()
    {var id=preview!.OperationId;executionId=Guid.NewGuid();await RunAsync(async progress=>Apply(await gateway!.ExecuteAsync(id,executionId.Value,progress,waiting!.Token)),true);}
    [RelayCommand(CanExecute=nameof(CanCheckResult))]private async Task CheckResultAsync()=>await RunAsync(async progress=>Apply(await gateway!.StatusAsync(executionId!.Value,progress,waiting!.Token)),true);
    private async Task RunAsync(Func<IProgress<DataCleanupStatus>,Task> action,bool mayHaveDeleted)
    {
        Busy=true;waiting?.Dispose();waiting=new();
        try{await action(new Progress<DataCleanupStatus>(Apply));}
        catch(OperationCanceledException){Message=mayHaveDeleted?"已停止等待；后台清理可能继续，请查询结果。":"已停止等待扫描；未提交清理。";}
        catch(Exception error){Message=(mayHaveDeleted?"清理结果尚未确认；请点击查询结果，避免重复提交。":"扫描未完成，未提交清理。")+" "+error.Message;}
        finally{Busy=false;NotifyCommands();}
    }
    private void Apply(DataCleanupStatus status)
    {
        Message=status.Message;Entries.Clear();foreach(var entry in status.Entries.OrderByDescending(x=>x.Selected).ThenByDescending(x=>x.Eligible))Entries.Add(new(entry,status.State));
        if(status.State=="Ready"){preview=status;Summary="本次将清理 "+FormatBytes(status.Entries.Where(x=>x.Selected).Sum(x=>x.Bytes??0))+" · "+status.Entries.Count(x=>x.Selected)+" 项";}
        else{preview=null;Summary=status.CountsComplete?$"{StateText(status.State)} · 已清理 {FormatBytes(status.DeletedBytes)} / {status.DeletedFiles} 个文件":$"{StateText(status.State)} · 删除数量未完整记录，请重新扫描";}
        if(status.OperationId==executionId&&status.State is not ("Running" or "Scanning"))executionId=null;
        NotifyCommands();
    }
    private void NotifyCommands(){OnPropertyChanged(nameof(CanEdit));ScanCommand.NotifyCanExecuteChanged();ExecuteCommand.NotifyCanExecuteChanged();CheckResultCommand.NotifyCanExecuteChanged();}
    public void StopWaiting()=>waiting?.Cancel();
    private static string StateText(string state)=>state switch{"Scanning"=>"正在扫描","Running"=>"正在清理","Completed"=>"清理完成","Interrupted"=>"未全部完成","Unavailable"=>"扫描不可用","Expired"=>"预览已过期",_=>"结果未知"};
    internal static string FormatBytes(long bytes)=>bytes>=1024L*1024*1024?$"{bytes/(1024d*1024*1024):N2} GB":bytes>=1024*1024?$"{bytes/(1024d*1024):N1} MB":$"{bytes/1024d:N1} KB";
}
