using System;
using System.Windows;
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Settings;

/// <summary>WeChat 设置子视图。字段和密码框始终保留在一个窗口实例中，锁定时由主窗口主动清理。</summary>
public partial class WeChatSettingsView : UserControl, ISettingsSecretView
{
    public WeChatSettingsView() => InitializeComponent();
    public void ClearInputs() => WeChatToken.Clear();
    private void OnWeChatToken(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel model) model.WeChat.SetToken(WeChatToken.Password); }
}
