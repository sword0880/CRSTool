using System;
using System.Windows;
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Settings;

/// <summary>Documents 设置子视图。字段和密码框始终保留在一个窗口实例中，锁定时由主窗口主动清理。</summary>
public partial class DocumentSettingsView : UserControl, ISettingsSecretView
{
    public DocumentSettingsView() => InitializeComponent();
    public void ClearInputs() { DocumentPasswordInput.Clear(); DocumentConfirmationInput.Clear(); }
    private void OnDocumentPassword(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel model) model.DocumentPassword = DocumentPasswordInput.Password; }
    private void OnDocumentConfirmation(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel model) model.DocumentPasswordConfirmation = DocumentConfirmationInput.Password; }
}
