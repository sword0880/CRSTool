using System;
using System.Windows;
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Settings;

/// <summary>DingTalk 设置子视图。字段和密码框始终保留在一个窗口实例中，锁定时由主窗口主动清理。</summary>
public partial class DingTalkSettingsView : UserControl, ISettingsSecretView
{
    public DingTalkSettingsView() => InitializeComponent();
    public void ClearInputs() { DingTalkToken.Clear(); DingTalkSecret.Clear(); }
    private void OnDingTalkToken(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel model) model.DingTalk.SetToken(DingTalkToken.Password); }
    private void OnDingTalkSecret(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel model) model.DingTalk.SetSigningSecret(DingTalkSecret.Password); }
}
