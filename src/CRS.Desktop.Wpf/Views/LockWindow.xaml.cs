using System.Windows;

namespace CRS.DesktopClient.Views;

/// <summary>只等待用户主动解锁，不取得旧模型、数据库密钥或业务数据。</summary>
public partial class LockWindow:Window
{
    public event Action? UnlockRequested;
    public LockWindow()=>InitializeComponent();
    private void OnUnlock(object sender,RoutedEventArgs e)=>UnlockRequested?.Invoke();
}
