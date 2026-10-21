using System;
using System.Windows;
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Settings;

/// <summary>Security 设置子视图。字段和密码框始终保留在一个窗口实例中，锁定时由主窗口主动清理。</summary>
public partial class SecuritySettingsView : UserControl, ISettingsSecretView
{
    public SecuritySettingsView() => InitializeComponent();
    public void ClearInputs() { SecurityPasswordInput.Clear(); SecurityCodeInput.Clear(); }
    private void OnSecurityPassword(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel model) model.SecurityPassword = SecurityPasswordInput.Password; }
    private void OnSecurityCode(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel model) model.SecurityCode = SecurityCodeInput.Password; }
}
