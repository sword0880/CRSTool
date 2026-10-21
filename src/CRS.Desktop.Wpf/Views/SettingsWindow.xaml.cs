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
    private void OnDatabaseManagementRequested(object? sender,EventArgs e) {OpenDatabaseManagement=true;Close();}
    private void OnPhoneEnrollmentRequired() {RequiresPhoneEnrollment=true; Close();}
    public SettingsWindow(SettingsViewModel model)
    {
        InitializeComponent(); DataContext=model; StorageSection.DatabaseManagementRequested+=OnDatabaseManagementRequested; Closing+=OnClosing; model.SecurityInputsCleared+=ClearSecurityInputs;
        // 新分类从顶部开始阅读；只改变视口，不重建表单或清除待保存字段。
        model.PropertyChanged+=OnModelPropertyChanged;
        model.PhoneEnrollmentRequired+=OnPhoneEnrollmentRequired;
        model.DocumentInputsCleared+=ClearDocumentInputs;
        model.EmailInputCleared+=ClearEmailInput;
        model.DingTalk.InputsCleared+=ClearDingTalkInputs;
        model.WeChat.InputsCleared+=ClearWeChatInputs;
        Loaded+=async (_,_)=>{await model.CheckHelloAvailabilityAsync();await model.LoadEmailSettingsAsync();await model.LoadWebhookSettingsAsync();};
        Closed+=(_,_)=> {StorageSection.DatabaseManagementRequested-=OnDatabaseManagementRequested;model.PropertyChanged-=OnModelPropertyChanged;model.EmailInputCleared-=ClearEmailInput;model.DocumentInputsCleared-=ClearDocumentInputs;model.PhoneEnrollmentRequired-=OnPhoneEnrollmentRequired; model.SecurityInputsCleared-=ClearSecurityInputs; model.ClearSecurityInputs(); ClearSecurityInputs();ClearEmailInput();};
    }
    private void OnModelPropertyChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(SettingsViewModel.SelectedSection))SectionContentScroll.ScrollToTop();
    }
    private void OnSectionSelected(object sender,RoutedPropertyChangedEventArgs<object> e)
    {
        if(DataContext is not SettingsViewModel model)return;
        // 点击分组直接进入首个子页；键盘和鼠标都沿用 TreeView 的标准导航。
        if(e.NewValue is SettingsSection section)model.SelectedSection=section;
        else if(e.NewValue is SettingsGroup group)
        {
            group.IsSelected=false;model.SelectedSection=group.Children.FirstOrDefault();
            if(model.SelectedSection is {} first)first.IsSelected=true;
        }
    }
    // 子视图各自保管 PasswordBox，窗口仍是敏感输入清理的唯一调度入口。
    private void ClearSecurityInputs() {SecuritySection.ClearInputs();DocumentsSection.ClearInputs();}
    private void ClearDocumentInputs()=>DocumentsSection.ClearInputs();
    private void ClearEmailInput()=>NotificationsSection.ClearInputs();
    private void ClearDingTalkInputs()=>DingTalkSection.ClearInputs();
    private void ClearWeChatInputs()=>WeChatSection.ClearInputs();
    /// <summary>系统锁定关闭设置窗口时清理凭证，允许中断普通的忙碌关闭门槛。</summary>
    public void AbortForLock() {locking=true; ((SettingsViewModel)DataContext).ClearSecurityInputs(); Close();}
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var model=(SettingsViewModel)DataContext;e.Cancel=!locking&&(model.State.IsBusy||model.DingTalk.Busy||model.WeChat.Busy);
        if(!e.Cancel){model.DingTalk.InputsCleared-=ClearDingTalkInputs;model.WeChat.InputsCleared-=ClearWeChatInputs;ClearDingTalkInputs();ClearWeChatInputs();}
    }
}
