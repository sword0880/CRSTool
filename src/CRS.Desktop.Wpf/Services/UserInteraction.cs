using Microsoft.Win32;
using System.Windows;

namespace CRS.DesktopClient.Services;

/// <summary>隔离系统对话框，视图模型不依赖窗口控件。</summary>
public interface IUserInteraction
{
    string[] PickFiles(string filter, bool multiple);
    string? SaveFile(string filter, string fileName);
    void ShowError(string message);
    void OpenFolder(string path);
}

public sealed class UserInteraction : IUserInteraction
{
    /// <summary>返回用户选择的文件路径。</summary>
    public string[] PickFiles(string filter, bool multiple)
    {
        var dialog = new OpenFileDialog { Filter = filter, Multiselect = multiple };
        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dialog.FileNames : [];
    }
    /// <summary>取得导出目标，取消时返回空。</summary>
    public string? SaveFile(string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = fileName };
        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
    /// <summary>显示已脱敏的操作提示。</summary>
    public void ShowError(string message) => MessageBox.Show(System.Windows.Application.Current.MainWindow, message, "请检查", MessageBoxButton.OK, MessageBoxImage.Warning);
    /// <summary>打开可编辑的配置目录。</summary>
    public void OpenFolder(string path) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
}
