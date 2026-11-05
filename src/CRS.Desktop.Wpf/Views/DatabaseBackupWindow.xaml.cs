using System.Windows;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views;

/// <summary>只承载数据库备份与恢复；密码输入不经普通文本绑定，锁定或关闭时立即清空。</summary>
public partial class DatabaseBackupWindow:Window
{
    private readonly DatabaseBackupViewModel model;
    private bool locking;
    public DatabaseBackupWindow(DatabaseBackupViewModel model)
    {
        this.model=model;InitializeComponent();Services.StartupWindowSizing.Attach(this);DataContext=model;
        model.SensitiveCleared+=ClearInputs;
        Closing+=(_,e)=>e.Cancel=model.IsBusy&&!locking;
        Closed+=(_,_)=>{model.SensitiveCleared-=ClearInputs;model.CancelPending();ClearInputs();};
    }
    public void AbortForLock(){locking=true;model.CancelPending();ClearInputs();Close();}
    private void ClearInputs(){RestoreSecretInput.Clear();RestorePasswordInput.Clear();}
    private void OnSecret(object sender,RoutedEventArgs e)=>model.RestoreSecret=RestoreSecretInput.Password;
    private void OnPassword(object sender,RoutedEventArgs e)=>model.NewPassword=RestorePasswordInput.Password;
    private void OnClose(object sender,RoutedEventArgs e)=>Close();
    private void OnSelectBackup(object sender,RoutedEventArgs e)
    {
        var dialog=new Microsoft.Win32.SaveFileDialog{Filter="加密备份|*.crsbak",FileName="CRS_Backup.crsbak"};
        if(dialog.ShowDialog(this)==true)model.BackupDestination=dialog.FileName;
    }
    private void OnSelectRestore(object sender,RoutedEventArgs e)
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Filter="加密备份|*.crsbak"};
        if(dialog.ShowDialog(this)==true)model.RestoreSource=dialog.FileName;
    }
    private void OnSelectTarget(object sender,RoutedEventArgs e)
    {
        var dialog=new Microsoft.Win32.OpenFolderDialog{Title="选择新的空目录用于恢复备份"};
        if(dialog.ShowDialog(this)==true)model.RestoreTarget=dialog.FolderName;
    }
}
