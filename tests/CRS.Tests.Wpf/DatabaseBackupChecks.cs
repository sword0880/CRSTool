using System.IO;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;

internal static class DatabaseBackupChecks
{
    internal sealed class Settings:IDesktopSettingsService
    {
        public DesktopSettings Current{get;private set;}=new("D:/synthetic-current","D:/synthetic-logs");
        public int Saves;public bool FailSave;
        public DesktopSettings Load()=>Current;
        public Task SaveAsync(DesktopSettings settings)
        {if(FailSave)throw new IOException("synthetic settings failure");Current=settings;Saves++;return Task.CompletedTask;}
    }
    public static async Task RunAsync()
    {
        var passed=0;void Check(bool value,string text){if(!value)throw new Exception(text);passed++;Console.WriteLine("PASS 数据库独立管理："+text);}
        var vault=new VaultInteractionChecks.FakeVault();await vault.CompleteEnrollmentAsync(true);var settings=new Settings();
        var model=new DatabaseBackupViewModel(vault,settings,"D:/active-database");
        Check(model.CurrentDirectory=="D:/active-database"&&!model.BackupCommand.CanExecute(null)&&!model.RestoreCommand.CanExecute(null),"实际打开目录与下次启动设置分开，未选路径不能操作");
        model.BackupDestination="D:/backup/current.crsbak";await model.BackupCommand.ExecuteAsync(null);
        Check(vault.BackupRequests.SequenceEqual([model.BackupDestination])&&settings.Saves==0&&vault.IsUnlocked,"创建备份只使用备份目标，不切换数据库或触发登录");
        model.RestoreSource="D:/backup/old.crsbak";model.RestoreTarget="D:/restored-new";
        await model.RestoreCommand.ExecuteAsync(null);
        Check(!model.RestoreStarted&&vault.IsUnlocked&&vault.RestoreRequests.Count==0,"缺少恢复凭证时不锁定或恢复");
        model.RestoreSecret="synthetic-backup-password";model.NewPassword="synthetic-new-master-password";
        await model.RestoreCommand.ExecuteAsync(null);
        Check(!model.RestoreStarted&&vault.IsUnlocked&&vault.RestoreRequests.Count==0,"未接入工作区关闭流程时不能恢复已打开数据库");
        var restricted=0;model.RestoreStarting+=()=>{restricted++;vault.Lock();};
        model.RestoreSecret="synthetic-recovery-key";model.NewPassword="synthetic-new-master-password";model.UseRecoveryKey=true;
        await model.RestoreCommand.ExecuteAsync(null);
        var request=vault.RestoreRequests.Single();
        Check(restricted==1&&!vault.IsUnlocked&&model.Restored&&request.Source==model.RestoreSource&&request.Source!=model.BackupDestination
            &&request.Target==model.RestoreTarget&&request.Recovery,"恢复先锁定旧库，使用独立备份来源、目标与恢复方式");
        Check(settings.Current.DatabaseDirectory==model.RestoreTarget&&settings.Saves==1,"成功恢复后才保存新目录");
        Check(model.RestoreSecret.Length==0&&model.NewPassword.Length==0&&!model.RestoreCommand.CanExecute(null)&&!model.BackupCommand.CanExecute(null),"恢复后清理凭证，禁止重复恢复或沿用旧库创建备份");
        await VerifyFailureAsync(Check);
        Console.WriteLine($"独立数据库与备份模型验证通过：{passed} 项；仅合成服务，不打开实际数据库。");
    }
    private static async Task VerifyFailureAsync(Action<bool,string> check)
    {
        var vault=new VaultInteractionChecks.FakeVault{FailRestore=true};await vault.CompleteEnrollmentAsync(true);var settings=new Settings();
        var model=new DatabaseBackupViewModel(vault,settings,settings.Current.DatabaseDirectory)
        {RestoreSource="D:/old.crsbak",RestoreTarget="D:/target",RestoreSecret="synthetic-secret",NewPassword="synthetic-new-password"};
        model.RestoreStarting+=vault.Lock;await model.RestoreCommand.ExecuteAsync(null);
        check(!model.Restored&&settings.Saves==0&&settings.Current.DatabaseDirectory=="D:/synthetic-current"&&!model.IsBusy&&model.RestoreSecret.Length==0,"恢复失败保留原启动目录并清理凭证");
        vault.FailRestore=false;settings.FailSave=true;model.RestoreSecret="synthetic-secret";model.NewPassword="synthetic-new-password";
        await model.RestoreCommand.ExecuteAsync(null);
        check(model.Restored&&model.Status.Contains("已恢复")&&model.Status.Contains("未能保存"),"恢复已提交但设置保存失败时如实说明，不误报恢复失败");
        vault=new VaultInteractionChecks.FakeVault{BeforeBackup=token=>Task.Delay(Timeout.Infinite,token)};await vault.CompleteEnrollmentAsync(true);
        model=new DatabaseBackupViewModel(vault,new Settings(),"D:/active"){BackupDestination="D:/backup.crsbak",RestoreSecret="synthetic",NewPassword="synthetic-password"};
        var backup=model.BackupCommand.ExecuteAsync(null);check(model.IsBusy&&!model.RestoreCommand.CanExecute(null),"备份执行时禁止并发恢复");
        model.CancelPending();await backup;
        check(!model.IsBusy&&model.RestoreSecret.Length==0&&model.NewPassword.Length==0&&model.Status.Contains("取消"),"锁定取消未完成操作并清理敏感输入");
    }
}
