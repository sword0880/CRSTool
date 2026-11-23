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
    Task<ImportDecision> ReviewImportAsync(ImportReview review, CancellationToken cancellation)
        => Task.FromResult(ImportDecision.Cancel);
    bool ConfirmArchiveHistory(HistoryItem task) => false;
    HistoricalRecalculationRequest? PickHistoricalRecalculation(HistoricalRecalculationPlan plan) => null;
    void ShowImportHistoryComparison(IReadOnlyList<ImportHistoryComparison> rows) { }
    string? PickFolder() => null;
    /// <summary>普通导出包含敏感明文，必须由用户明确确认；测试替身可自行控制确认结果。</summary>
    bool ConfirmPlaintextExport()=>false;
    /// <summary>解密恢复产生明文，需明确告知用户选择可信位置。</summary>
    bool ConfirmPlaintextRestore()=>false;
}

public sealed class UserInteraction : IUserInteraction
{
    public Task<ImportDecision> ReviewImportAsync(ImportReview review, CancellationToken cancellation)
        => System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = new Views.ImportReviewWindow(review) { Owner = System.Windows.Application.Current.MainWindow };
            using var registration = cancellation.Register(() => window.Dispatcher.InvokeAsync(() =>
            {
                if (window.IsVisible) window.Close();
            }));
            cancellation.ThrowIfCancellationRequested();
            window.ShowDialog();
            return window.Decision;
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellation).Task;

    public bool ConfirmArchiveHistory(HistoryItem task) => MessageBox.Show(System.Windows.Application.Current.MainWindow,
        $"归档 {task.Broker} {task.Year} 年、{CRS.Timing.BeijingTime.Display(task.CreatedUtc, "yyyy-MM-dd HH:mm:ss")} 保存的任务？\n\n"
        + "同批报表的任务可能使用了不同的核算条件，请核对要保留哪一条。归档后仍可打开或恢复。",
        "归档重复任务", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public HistoricalRecalculationRequest? PickHistoricalRecalculation(HistoricalRecalculationPlan plan)
    {
        var window = new Views.HistoricalRecalculationWindow(plan) { Owner = System.Windows.Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.Request : null;
    }

    public void ShowImportHistoryComparison(IReadOnlyList<ImportHistoryComparison> rows)
        => new Views.ImportHistoryComparisonWindow(rows) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();

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
