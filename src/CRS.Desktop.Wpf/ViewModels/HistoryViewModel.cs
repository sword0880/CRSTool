using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CRS.DesktopClient.ViewModels;

/// <summary>历史分页、冻结结果恢复及汇总另存，原任务保持不变。</summary>
public partial class HistoryViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    public ObservableCollection<HistoryItem> Rows { get; } = [];
    public ObservableCollection<HistoryItem> RecentRows { get; } = [];
    public ObservableCollection<HistoryTaskRow> SelectableRows { get; } = [];
    public ObservableCollection<HistoryItem> AggregationSelection { get; } = [];
    [ObservableProperty] private HistoryTaskRow? selectedRow;
    [ObservableProperty] private bool ownershipConfirmed;
    private readonly Action? aggregated;
    public string AggregationHint => AggregationSelection.Count == 0 ? "勾选同年度的账户任务，可跨页保留选择。"
        : AggregationSelection.Select(t => t.Year).Distinct().Count() != 1 ? $"已选 {AggregationSelection.Count} 份 · 年度不一致，请移除其他年度的任务。"
        : $"已选 {AggregationSelection.Count} 份 · {AggregationSelection[0].Year} 年 · 来源任务保持不变。";
    [ObservableProperty] private HistoryItem? selected;
    [ObservableProperty] private int pageNumber = 1;
    [ObservableProperty] private int totalCount;
    private const int PageSize = 50;
    private CancellationTokenSource? cancellation;
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public string PageLabel => $"第 {PageNumber} / {PageCount} 页 · 共 {TotalCount} 份任务";
    public HistoryViewModel(WorkspaceState state, Action? aggregated = null)
    {
        State = state; this.aggregated = aggregated;
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) RefreshCommands(); };
        AggregationSelection.CollectionChanged += (_, _) =>
        {
            // 选择范围变化后必须重新确认账户归属，不沿用先前选择的确认。
            OwnershipConfirmed = false; OnPropertyChanged(nameof(AggregationHint)); RefreshCommands();
        };
    }
    partial void OnSelectedChanged(HistoryItem? value) { SelectedRow = SelectableRows.FirstOrDefault(r => r.Task.Id == value?.Id); RefreshCommands(); }
    partial void OnSelectedRowChanged(HistoryTaskRow? value) { if (value is not null || Selected is not null && Rows.Contains(Selected)) Selected = value?.Task; }
    partial void OnOwnershipConfirmedChanged(bool value) => AggregateAnnualCommand.NotifyCanExecuteChanged();
    partial void OnPageNumberChanged(int value) { OnPropertyChanged(nameof(PageLabel)); RefreshCommands(); }
    partial void OnTotalCountChanged(int value) { OnPropertyChanged(nameof(PageCount)); OnPropertyChanged(nameof(PageLabel)); RefreshCommands(); }
    private bool CanOperate() => State.IsIdle;
    private bool CanOpen() => State.IsIdle && Selected is not null;
    private bool CanSourceOperation() => CanOpen() && !Selected!.IsAnnualAggregate;
    private bool CanPrevious() => State.IsIdle && PageNumber > 1;
    private bool CanNext() => State.IsIdle && PageNumber < PageCount;
    private bool CanCancel() => State.IsBusy && cancellation is not null;
    private bool CanAggregate() => State.IsIdle && OwnershipConfirmed && AggregationSelection.Count is >= 2 and <= 100
        && AggregationSelection.Select(t => t.Year).Distinct().Count() == 1;
    private void RefreshCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged(); NextPageCommand.NotifyCanExecuteChanged();
        VerifyOriginalsCommand.NotifyCanExecuteChanged(); ReplayCommand.NotifyCanExecuteChanged();
        ReviewForeignCreditCommand.NotifyCanExecuteChanged();
        CancelOperationCommand.NotifyCanExecuteChanged();
        AggregateAnnualCommand.NotifyCanExecuteChanged(); ClearAggregationSelectionCommand.NotifyCanExecuteChanged(); RemoveAggregationTaskCommand.NotifyCanExecuteChanged();
    }
    /// <summary>加载最近计算任务。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RefreshAsync() => await State.RunAsync(async () =>
    {
        await LoadPageAsync(1);
    });
    /// <summary>换页不改变概览的最近任务，选中项仅对当前页有效。</summary>
    private async Task LoadPageAsync(int pageNumber)
    {
        var page = await State.UseCases.QueryHistoryAsync(pageNumber, PageSize);
        Selected = null; Rows.Clear(); foreach (var row in page.Rows) Rows.Add(row);
        SelectableRows.Clear();
        foreach (var task in page.Rows)
        {
            var row = new HistoryTaskRow(task, AggregationSelection.Any(t => t.Id == task.Id));
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(row.IsIncluded)) UpdateAggregationSelection(row); };
            SelectableRows.Add(row);
        }
        TotalCount = page.TotalCount; PageNumber = page.PageNumber;
        if (PageNumber == 1) { RecentRows.Clear(); foreach (var row in page.Rows.Take(5)) RecentRows.Add(row); }
    }
    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private async Task PreviousPageAsync() => await State.RunAsync(() => LoadPageAsync(PageNumber - 1));
    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextPageAsync() => await State.RunAsync(() => LoadPageAsync(PageNumber + 1));
    /// <summary>按快照编号恢复结果，前台导出沿用冻结汇率。</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        var id = Selected!.Id;
        await State.RunAsync(async () => await State.DisplayAsync(await State.UseCases.LoadAsync(id)));
    }

    /// <summary>按后台给出的原件要求分别选择报告和期初文件，取消选择不触发校验。</summary>
    [RelayCommand(CanExecute = nameof(CanSourceOperation))]
    private async Task VerifyOriginalsAsync()
    {
        var task = Selected!;
        OriginalFileRequirements? requirements = null;
        if (!await State.RunAsync(async () => requirements = await State.UseCases.OriginalFileRequirementsAsync(task.Id))) return;
        State.Status = "请选择原始报告：" + string.Join("、", requirements!.Reports);
        var files = State.Interaction.PickFiles(task.Broker == "FUTU" ? "富途报告|*.xlsx;*.pdf" : "IBKR 活动报告|*.xml", true);
        if (files.Length == 0) { State.Status = "已取消原件选择。"; return; }
        string? opening = null;
        if (requirements.OpeningFile is { } name)
        {
            State.Status = $"请选择期初原件：{name}";
            opening = State.Interaction.PickFiles("期初 LOT XML|*.xml", false).FirstOrDefault();
            if (opening is null) { State.Status = "已取消期初原件选择。"; return; }
        }
        using var cts = new CancellationTokenSource(); cancellation = cts;
        try
        {
            await State.RunAsync(async () =>
            {
                State.Status = "正在校验原件与历史摘要……";
                await State.UseCases.VerifyOriginalFilesAsync(task.Id, files, opening, cts.Token);
                State.Status = "原件与历史摘要一致，原任务保持不变。";
            });
        }
        finally { cancellation = null; RefreshCommands(); }
    }

    /// <summary>规范化快照复算先做完整输出比对，成功后另存并打开新运行。</summary>
    [RelayCommand(CanExecute = nameof(CanSourceOperation))]
    private async Task ReplayAsync()
    {
        var id = Selected!.Id;
        using var cts = new CancellationTokenSource(); cancellation = cts;
        try
        {
            await State.RunAsync(async () =>
            {
                State.Status = "正在使用冻结汇率复算并比对历史输出……";
                var replay = await State.UseCases.ReplayAsync(id, cts.Token);
                await State.DisplayAsync(replay);
                await LoadPageAsync(1); Selected = Rows.FirstOrDefault(r => r.Id == replay.SnapshotId);
                State.Status = "复算比对通过，已另存新任务并保留原任务。";
            });
        }
        finally { cancellation = null; RefreshCommands(); }
    }
    /// <summary>选择抵免明细，由后台验证国家分配和凭证后另存，前台不读取文件正文。</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task ReviewForeignCreditAsync()
    {
        var id = Selected!.Id;
        var path = State.Interaction.PickFiles("抵免明细 JSON|*.json", false).FirstOrDefault();
        if (path is null) { State.Status = "已取消抵免明细选择。"; return; }
        using var cts = new CancellationTokenSource(); cancellation = cts;
        try
        {
            var success = await State.RunAsync(async () =>
            {
                State.Status = "正在核对所得国家分配、税款与凭证摘要……";
                var result = await State.UseCases.ReviewForeignCreditAsync(id, path, cts.Token);
                await State.DisplayAsync(result);
                await LoadPageAsync(1); Selected = Rows.FirstOrDefault(t => t.Id == result.SnapshotId);
                State.Status = "抵免复核已另存。请核对凭证真实性、协定及法定折算口径。";
            });
            if (success) aggregated?.Invoke();
        }
        finally { cancellation = null; RefreshCommands(); }
    }

    /// <summary>取消本页原件校验或复算请求，后台在保存之前检查取消。</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelOperation() => cancellation?.Cancel();

    /// <summary>勾选跨页累计任务；年度汇总本身不能再次纳入，防止重复统计来源。</summary>
    private void UpdateAggregationSelection(HistoryTaskRow row)
    {
        if (row.IsIncluded && !row.CanInclude) { row.IsIncluded = false; return; }
        var existing = AggregationSelection.FirstOrDefault(t => t.Id == row.Task.Id);
        if (row.IsIncluded && existing is null) AggregationSelection.Add(row.Task);
        else if (!row.IsIncluded && existing is not null) AggregationSelection.Remove(existing);
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void RemoveAggregationTask(HistoryItem task)
    {
        AggregationSelection.Remove(task);
        var row = SelectableRows.FirstOrDefault(r => r.Task.Id == task.Id);
        if (row is not null) row.IsIncluded = false;
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void ClearAggregationSelection()
    {
        AggregationSelection.Clear(); foreach (var row in SelectableRows) row.IsIncluded = false;
    }
    /// <summary>提交不可变选择，后台汇总原币事实并另存；成功后展示年度结果。</summary>
    [RelayCommand(CanExecute = nameof(CanAggregate))]
    private async Task AggregateAnnualAsync()
    {
        var ids = AggregationSelection.Select(t => t.Id).ToArray();
        var confirmed = OwnershipConfirmed;
        using var cts = new CancellationTokenSource(); cancellation = cts;
        try
        {
            var success = await State.RunAsync(async () =>
            {
                State.Status = "正在校验账户范围并汇总年度收入、收益和税款……";
                var result = await State.UseCases.AggregateAnnualAsync(ids, confirmed, cts.Token);
                await State.DisplayAsync(result); ClearAggregationSelection();
                await LoadPageAsync(1); Selected = Rows.FirstOrDefault(t => t.Id == result.SnapshotId);
                State.Status = "年度汇总已另存，来源任务保持不变。";
            });
            if (success) aggregated?.Invoke();
        }
        finally { cancellation = null; RefreshCommands(); }
    }
}
