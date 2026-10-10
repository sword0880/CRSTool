using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CRS.DesktopClient.ViewModels;

/// <summary>展示当前年度概览，数字和状态来自实际结果。</summary>
public partial class DashboardViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    public HistoryViewModel History { get; }
    private readonly Action<string> navigate;
    public string TradeCount => State.Result is { } r ? r.ImportedTradeCount is int count ? count.ToString("N0") : "未登记" : "—";
    public string IssueCount => State.Result?.Issues.Count.ToString("N0") ?? "—";
    public string MatchCount => State.Result?.Matches.Count.ToString("N0") ?? "—";
    public string TaxStatus => State.Result is { } r ? ResultLabels.Calculation(r.CalculationStatus) : "尚未计算";
    public string TaxHint => State.Result is { } r ? ResultLabels.Usage(r.UsageLabel) : "等待导入年度资料";
    public ObservableCollection<HistoryItem> RecentTasks { get; } = [];
    public DashboardViewModel(WorkspaceState state, HistoryViewModel history, Action<string> navigate)
    {
        State = state; History = history; this.navigate = navigate;
        state.ResultChanged += () => { OnPropertyChanged(nameof(TradeCount)); OnPropertyChanged(nameof(IssueCount)); OnPropertyChanged(nameof(MatchCount)); OnPropertyChanged(nameof(TaxStatus)); OnPropertyChanged(nameof(TaxHint)); };
        // 历史换页不应把旧任务误显示成“最新导入任务”。
        history.RecentRows.CollectionChanged += (_, _) => { RecentTasks.Clear(); foreach (var row in history.RecentRows) RecentTasks.Add(row); };
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) OpenTaskCommand.NotifyCanExecuteChanged(); };
    }
    /// <summary>从概览卡片跳转至对应业务模块。</summary>
    [RelayCommand] private void Navigate(string page) => navigate(page);
    private bool CanOpenTask() => State.IsIdle;
    /// <summary>概览任务可直接恢复冻结结果并进入税务核算页。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTask))]
    private async Task OpenTaskAsync(HistoryItem task)
    {
        History.Selected = task; await History.OpenCommand.ExecuteAsync(null);
        if (State.Result?.SnapshotId == task.Id) navigate("tax");
    }
}
