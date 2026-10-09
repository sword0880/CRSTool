using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CRS.DesktopClient.ViewModels;

/// <summary>历史索引与冻结结果恢复，不重新计算历史金额。</summary>
public partial class HistoryViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    public ObservableCollection<HistoryItem> Rows { get; } = [];
    [ObservableProperty] private HistoryItem? selected;
    public HistoryViewModel(WorkspaceState state)
    {
        State = state;
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) { RefreshCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged(); } };
    }
    partial void OnSelectedChanged(HistoryItem? value) => OpenCommand.NotifyCanExecuteChanged();
    private bool CanOperate() => State.IsIdle;
    private bool CanOpen() => State.IsIdle && Selected is not null;
    /// <summary>加载最近计算任务。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RefreshAsync() => await State.RunAsync(async () =>
    {
        var rows = await State.UseCases.HistoryAsync(); Rows.Clear(); foreach (var row in rows) Rows.Add(row);
    });
    /// <summary>按快照编号恢复结果，前台导出沿用冻结汇率。</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        var id = Selected!.Id;
        await State.RunAsync(async () => await State.DisplayAsync(await State.UseCases.LoadAsync(id)));
    }
}
