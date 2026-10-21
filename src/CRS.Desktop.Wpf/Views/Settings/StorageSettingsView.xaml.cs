using System;
using System.Windows;
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Settings;

/// <summary>Storage 设置子视图。字段和密码框始终保留在一个窗口实例中，锁定时由主窗口主动清理。</summary>
public partial class StorageSettingsView : UserControl
{
    public StorageSettingsView() => InitializeComponent();
    public event EventHandler? DatabaseManagementRequested;
    private void OnDatabaseManagement(object sender, RoutedEventArgs e) => DatabaseManagementRequested?.Invoke(this, EventArgs.Empty);
}
