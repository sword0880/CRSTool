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
    string? PickFolder() => null;
    /// <summary>普通导出包含敏感明文，必须由用户明确确认；测试替身可自行控制确认结果。</summary>
    bool ConfirmPlaintextExport()=>false;
    /// <summary>解密恢复产生明文，需明确告知用户选择可信位置。</summary>
    bool ConfirmPlaintextRestore()=>false;
    bool ConfirmPerformanceOnlineUpdates()=>false;
}

public sealed class UserInteraction : IUserInteraction
{
    public bool ConfirmPerformanceOnlineUpdates()=>MessageBox.Show(System.Windows.Application.Current.MainWindow,
        "允许本次解锁期间联网获取指数历史报价和对比用汇率？\n\n数据来源：Yahoo Finance。请求只发送指数代码（如标普500）、期间和币种，不发送账户、持仓或交易。选择允许后，重算可按需查询；切回仅离线或锁定即撤销授权并取消未完成的请求。",
        "授权在线更新",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
    public bool ConfirmPlaintextRestore()=>MessageBox.Show(System.Windows.Application.Current.MainWindow,
        "解密后将在你选择的位置新建文件夹，里面的报表和结转资料为明文。确认恢复到你信任的位置？","解密恢复",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes;
    public bool ConfirmPlaintextExport()=>MessageBox.Show(System.Windows.Application.Current.MainWindow,
        "导出文件不会被保险库加密，可能包含账户、交易和成本信息。确认保存到你信任的位置？","明文导出",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes;
    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择目录", Multiselect = false };
        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }
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
