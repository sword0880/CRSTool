using System.ComponentModel;
using System.Windows;
using CRS.DesktopClient.ViewModels;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views;

public partial class SettingsWindow : FluentWindow
{
    private bool locking;
    public bool RequiresPhoneEnrollment {get;private set;}
    public bool OpenDatabaseManagement {get;private set;}
    private void OnDatabaseManagement(object sender,RoutedEventArgs e) {OpenDatabaseManagement=true;Close();}
    private void OnPhoneEnrollmentRequired() {RequiresPhoneEnrollment=true; Close();}
    public SettingsWindow(SettingsViewModel model)
    {
        InitializeComponent(); DataContext=model; Closing+=OnClosing; model.SecurityInputsCleared+=ClearSecurityInputs;
        model.PhoneEnrollmentRequired+=OnPhoneEnrollmentRequired;
        model.DocumentInputsCleared+=ClearDocumentInputs;
        Loaded+=async (_,_)=>await model.CheckHelloAvailabilityAsync();
        Closed+=(_,_)=> {model.DocumentInputsCleared-=ClearDocumentInputs;model.PhoneEnrollmentRequired-=OnPhoneEnrollmentRequired; model.SecurityInputsCleared-=ClearSecurityInputs; model.ClearSecurityInputs(); ClearSecurityInputs();};
    }
    private void ClearSecurityInputs() {SecurityPasswordInput.Clear(); SecurityCodeInput.Clear();DocumentPasswordInput.Clear();DocumentConfirmationInput.Clear();}
    private void OnDocumentPassword(object sender,RoutedEventArgs e) {if(DataContext is SettingsViewModel model)model.DocumentPassword=DocumentPasswordInput.Password;}
    private void OnDocumentConfirmation(object sender,RoutedEventArgs e) {if(DataContext is SettingsViewModel model)model.DocumentPasswordConfirmation=DocumentConfirmationInput.Password;}
    private void ClearDocumentInputs() {DocumentPasswordInput.Clear();DocumentConfirmationInput.Clear();}
    /// <summary>系统锁定关闭设置窗口时清理凭证，允许中断普通的忙碌关闭门槛。</summary>
    public void AbortForLock() {locking=true; ((SettingsViewModel)DataContext).ClearSecurityInputs(); Close();}
    private void OnSecurityPassword(object sender,RoutedEventArgs e) {if(DataContext is SettingsViewModel model) model.SecurityPassword=SecurityPasswordInput.Password;}
    private void OnSecurityCode(object sender,RoutedEventArgs e) {if(DataContext is SettingsViewModel model) model.SecurityCode=SecurityCodeInput.Password;}
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = !locking && ((SettingsViewModel)DataContext).State.IsBusy;
}
