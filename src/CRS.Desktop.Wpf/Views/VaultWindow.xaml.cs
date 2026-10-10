using System.ComponentModel;
using System.Windows;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views;

/// <summary>密码控件只传给本会话，完成操作和关闭时清空；不访问密码学和持久化实现。</summary>
public partial class VaultWindow : Window
{
    private readonly VaultViewModel model;
    private bool locking;
    private readonly System.Windows.Threading.DispatcherTimer expiry;
    public VaultWindow(VaultViewModel model)
    {
        InitializeComponent(); this.model=model; DataContext=model;
        model.Ready+=Ready; model.PropertyChanged+=Changed; model.SensitiveCleared+=Clear;
        expiry=new(TimeSpan.FromSeconds(5),System.Windows.Threading.DispatcherPriority.Background,(_,_)=>model.CheckAuthenticationExpiry(),Dispatcher);
        Closed+=(_,_)=> {expiry.Stop(); model.Ready-=Ready; model.PropertyChanged-=Changed; model.SensitiveCleared-=Clear; Clear(); model.ClearSensitiveDisplay();};
        Closing+=(_,e)=> {if(model.IsBusy && !locking) e.Cancel=true;};
    }
    /// <summary>自动锁定可关闭安全对话框，先清除密码控件，再由应用销毁密钥会话。</summary>
    public void AbortForLock() {locking=true; Clear(); Close();}
    private void Ready()=>DialogResult=true;
    private void Changed(object? sender,PropertyChangedEventArgs e) {if(e.PropertyName==nameof(model.IsBusy) && !model.IsBusy) Clear();}
    private void Clear() {PasswordInput.Clear(); NewPasswordInput.Clear(); SecretInput.Clear(); CodeInput.Clear(); MfaRecoveryInput.Clear(); model.Password=""; model.NewPassword=""; model.Secret=""; model.Code=""; model.MfaRecoveryCode="";}
    private void OnCode(object sender,RoutedEventArgs e) {if(DataContext is VaultViewModel m) m.Code=CodeInput.Password;}
    private void OnMfaRecovery(object sender,RoutedEventArgs e) {if(DataContext is VaultViewModel m) m.MfaRecoveryCode=MfaRecoveryInput.Password;}
    private void OnPassword(object sender,RoutedEventArgs e) {if(DataContext is VaultViewModel m) m.Password=PasswordInput.Password;}
    private void OnNewPassword(object sender,RoutedEventArgs e) {if(DataContext is VaultViewModel m) m.NewPassword=NewPasswordInput.Password;}
    private void OnSecret(object sender,RoutedEventArgs e) {if(DataContext is VaultViewModel m) m.Secret=SecretInput.Password;}
    private void OnSelectBackup(object sender,RoutedEventArgs e)
    {
        if(model.IsBusy) return;
        if(model.IsUnlocked) {var dialog=new Microsoft.Win32.SaveFileDialog {Filter="加密备份|*.crsbak",FileName="CRS_Backup.crsbak"}; if(dialog.ShowDialog(this)==true) model.BackupPath=dialog.FileName;}
        else {var dialog=new Microsoft.Win32.OpenFileDialog {Filter="加密备份|*.crsbak"}; if(dialog.ShowDialog(this)==true) model.BackupPath=dialog.FileName;}
    }
}
