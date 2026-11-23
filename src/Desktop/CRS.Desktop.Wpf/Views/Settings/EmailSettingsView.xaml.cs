using System;
using System.Windows;
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Settings;

/// <summary>Email 设置子视图。字段和密码框始终保留在一个窗口实例中，锁定时由主窗口主动清理。</summary>
public partial class EmailSettingsView : UserControl, ISettingsSecretView
{
    public EmailSettingsView() => InitializeComponent();
    public void ClearInputs() => EmailPasswordInput.Clear();
    private void OnEmailPassword(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel model) model.SetEmailPassword(EmailPasswordInput.Password); }
}
