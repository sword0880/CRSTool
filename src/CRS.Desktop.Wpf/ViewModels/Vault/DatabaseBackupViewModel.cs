using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;

namespace CRS.DesktopClient.ViewModels;

/// <summary>独立的数据库备份/恢复会话；不提供登录、验证码或身份验证命令。</summary>
public partial class DatabaseBackupViewModel(IVaultService vault,IDesktopSettingsService settings,string currentDirectory):ObservableObject
{
    public string CurrentDirectory{get;}=currentDirectory;
    public string CapacityHint{get;}=DescribeCapacity(vault.BackupCapacity);
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(BackupCommand))]private string backupDestination="";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(RestoreCommand))]private string restoreSource="";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(RestoreCommand))]private string restoreTarget="";
    [ObservableProperty]private bool useRecoveryKey;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(IsIdle)),NotifyCanExecuteChangedFor(nameof(BackupCommand)),NotifyCanExecuteChangedFor(nameof(RestoreCommand))]private bool isBusy;
    [ObservableProperty]private string status="创建独立加密备份，或将已有备份恢复到新的空目录。";
    public string RestoreSecret{get;set;}="";
    public string NewPassword{get;set;}="";
    public bool IsIdle=>!IsBusy;
    public bool RestoreStarted{get;private set;}
    public bool Restored{get;private set;}
    public event Action? RestoreStarting;
    public event Action? SensitiveCleared;
    private bool CanBackup()=>!IsBusy&&!RestoreStarted&&vault.IsUnlocked&&!string.IsNullOrWhiteSpace(BackupDestination);
    private bool CanRestore()=>!IsBusy&&!Restored&&!string.IsNullOrWhiteSpace(RestoreSource)&&!string.IsNullOrWhiteSpace(RestoreTarget);
    private static string DescribeCapacity(VaultBackupCapacity? capacity)
    {
        if(capacity is null)return "备份使用当前已解锁的数据库，已有备份文件不会被覆盖。";
        var text=$"当前数据约 {capacity.DatabaseBytes/1048576d:F1} MiB，备份支持上限 {capacity.MaximumBytes/1073741824d:F0} GiB。";
        return text+(capacity.ExceedsLimit?" 已超出备份上限，请先处理容量。":capacity.NearLimit?" 已接近上限，请及时规划容量。":" 已有备份文件不会被覆盖。");
    }
    [RelayCommand(CanExecute=nameof(CanBackup))]
    private async Task BackupAsync(CancellationToken token)=>await RunAsync(async()=>
    {
        Status="正在创建并校验加密备份，请稍候。";
        await vault.BackupAsync(BackupDestination,token);
        Status="加密备份已验证并保存。恢复时使用备份时的主密码或恢复密钥。";
    });
    [RelayCommand(CanExecute=nameof(CanRestore))]
    private async Task RestoreAsync(CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(RestoreSecret)||NewPassword.Length is <12 or >1024)
        {Status="请填写备份恢复凭证，并设置12—1024个字符的新主密码。";return;}
        var source=RestoreSource;var target=RestoreTarget;var secret=RestoreSecret;var password=NewPassword;var useRecovery=UseRecoveryKey;
        await RunAsync(async()=>
        {
            token.ThrowIfCancellationRequested();
            if(!RestoreStarted)
            {
                if(vault.IsUnlocked&&RestoreStarting is null)throw new CrsException("当前数据库需要先关闭，再恢复备份。");
                RestoreStarted=true;RestoreStarting?.Invoke();
            }
            if(vault.IsUnlocked)throw new CrsException("当前数据库尚未关闭，未开始恢复。");
            Status="正在校验并恢复加密备份，请稍候。";
            await vault.RestoreAsync(source,target,secret,useRecovery,password,token);Restored=true;
            Status="备份已恢复到新位置。关闭此窗口后重新验证身份，打开恢复后的数据库。";
            try{await settings.SaveAsync(settings.Load() with{DatabaseDirectory=target});}
            catch(Exception){Status="备份已恢复，但默认数据库位置未能保存。请在数据库设置中选择恢复目录；原数据库保留。";}
        });
    }
    private async Task RunAsync(Func<Task> action)
    {
        IsBusy=true;
        try{await action();}
        catch(OperationCanceledException){Status="操作已取消。";}
        catch(Exception error){Status=error is CrsException?error.Message:"数据库操作未完成，请检查备份文件、恢复凭证和目录权限。";}
        finally{ClearSensitive();IsBusy=false;}
    }
    public void ClearSensitive(){RestoreSecret="";NewPassword="";SensitiveCleared?.Invoke();}
    public void CancelPending(){BackupCommand.Cancel();RestoreCommand.Cancel();ClearSensitive();}
}
