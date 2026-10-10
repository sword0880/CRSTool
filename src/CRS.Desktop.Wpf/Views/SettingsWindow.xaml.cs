using System.ComponentModel;
using System.Windows;
using CRS.DesktopClient.ViewModels;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(SettingsViewModel model) { InitializeComponent(); DataContext = model; Closing += OnClosing; }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = ((SettingsViewModel)DataContext).State.IsBusy;
}
