using CRS.Maintenance;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

/// <summary>任务中心与底栏共用后台任务摘要，不传递清理路径，也不据扫描量推算完成百分比。</summary>
internal static class CleanupTaskPresentation
{
    internal static bool Active(DataCleanupTask? task)=>task?.State is "Queued" or "Scanning" or "Running";
    internal static string StateText(DataCleanupTask task)=>task.State switch
    {
        "Queued"=>"等待执行",
        "Scanning"=>task.Progress?.Phase is "Verifying" or "VerifyingBackups"?"正在核验":"正在扫描",
        "Running"=>task.Progress?.Phase switch
        {
            "Verifying" or "VerifyingCleanup"=>"正在核验","Deleting"=>"正在清理",
            "ScanningRemainders"=>"正在核对残留","RemovingDirectories"=>"正在整理目录","SavingResults"=>"正在保存结果",_=>"正在执行"
        },
        _=>"状态待确认"
    };
    internal static string Detail(DataCleanupTask task)
    {
        var detail=StateText(task);
        if(task.State=="Scanning"&&task.Progress is {} progress)
            detail+=$" · 已扫描 {progress.ScannedFiles:N0} 个文件 / {DataCleanupViewModel.FormatBytes(progress.ScannedBytes)}";
        if(task.Progress is{VerifiedBytes:>0} verified)detail+=" · 校验已读取 "+DataCleanupViewModel.FormatBytes(verified.VerifiedBytes);
        if(task.State=="Running")detail+=task.CountsComplete?$" · 已清理 {task.DeletedFiles:N0} 个文件 / {DataCleanupViewModel.FormatBytes(task.DeletedBytes)}":" · 删除数量未完整记录";
        return detail;
    }
    internal static TaskActivityItem Activity(DataCleanupTask task,bool verified)=>new(
        new BackgroundTaskActivityRow("后台服务","data-cleanup",task.Origin=="Automatic"?"后台资料清理（自动）":"后台资料清理（手动）",
            task.State=="Queued"?"Waiting":"Running","DataCleanup",RequestId:task.Id,QueuePosition:task.State=="Queued"?1:null),
        (verified?"":"上次状态：")+Detail(task),task.SubmittedAt);
}
