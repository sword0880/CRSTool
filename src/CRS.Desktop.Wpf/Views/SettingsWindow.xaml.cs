using System.ComponentModel;
using System.Windows;
using CRS.DesktopClient.ViewModels;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views;

public partial class SettingsWindow : FluentWindow
{
    private bool locking;
    public SettingsWindow(SettingsViewModel model)
    {
        InitializeComponent(); DataContext=model; Closing+=OnClosing; model.SecurityInputsCleared+=ClearSecurityInputs;
        Closed+=(_,_)=> {model.SecurityInputsCleared-=ClearSecurityInputs; model.ClearSecurityInputs(); ClearSecurityInputs();};
    }
    private void ClearSecurityInputs() {SecurityPasswordInput.Clear(); SecurityCodeInput.Clear();}
    /// <summary>系统锁定关闭设置窗口时清理凭证，允许中断普通的忙碌关闭门槛。</summary>
    public void AbortForLock() {locking=true; ((SettingsViewModel)DataContext).ClearSecurityInputs(); Close();}
    private void OnSecurityPassword(object sender,RoutedEventArgs e) {if(DataContext is SettingsViewModel model) model.SecurityPassword=SecurityPasswordInput.Password;}
    private void OnSecurityCode(object sender,RoutedEventArgs e) {if(DataContext is SettingsViewModel model) model.SecurityCode=SecurityCodeInput.Password;}
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = !locking && ((SettingsViewModel)DataContext).State.IsBusy;
}
