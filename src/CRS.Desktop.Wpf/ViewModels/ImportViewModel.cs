using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CRS.DesktopClient.ViewModels;

/// <summary>管理导入表单、期初确认及 Flex 会话，后台执行导入与计算。</summary>
public partial class ImportViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    private readonly Action calculated;
    private string[] files = [];
    private string? futuDividendFile;
    private string[] futuTradeFiles = [];
    private string? openingPath;
    private CancellationTokenSource? cancellation;
    [ObservableProperty] private int year;
    [ObservableProperty] private string broker = "IBKR";
    [ObservableProperty] private bool useCarry;
    [ObservableProperty] private bool openingZero;
    [ObservableProperty] private bool scopeConfirmed;
    [ObservableProperty] private bool saveCanonicalSnapshot;
    [ObservableProperty] private string reportsLabel = "尚未选择报表";
    [ObservableProperty] private string futuDividendLabel = "尚未选择股息收入文件";
    [ObservableProperty] private string futuTradesLabel = "尚未选择交易明细文件";
    [ObservableProperty] private string openingLabel = "尚未选择期初资料";
    [ObservableProperty] private string queryId = "";
    [ObservableProperty] private string token = "";
    [ObservableProperty] private DateTime? from;
    [ObservableProperty] private DateTime? to;
    public string[] Brokers { get; } = ["IBKR", "FUTU"];
    public bool IsIbkr => Broker == "IBKR";
    public bool IsFutu => !IsIbkr;
    private string[] SelectedFiles => IsIbkr ? files : (futuDividendFile is null ? futuTradeFiles : new[] { futuDividendFile }.Concat(futuTradeFiles).ToArray());

    /// <summary>创建表单并在共享忙碌状态变化时刷新命令。</summary>
    public ImportViewModel(WorkspaceState state, Action calculated)
    {
        State = state; this.calculated = calculated; year = state.Year; SetDates();
        queryId = State.UseCases.ReadQueryId();
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) RefreshCommands(); };
    }
    partial void OnYearChanged(int value) { State.Year = value; SetDates(); Invalidate(); }
    partial void OnBrokerChanged(string value)
    {
        files = []; openingPath = null; ReportsLabel = "尚未选择报表"; OpeningLabel = "尚未选择期初资料";
        futuDividendFile = null; futuTradeFiles = [];
        FutuDividendLabel = "尚未选择股息收入文件"; FutuTradesLabel = "尚未选择交易明细文件";
        OpeningZero = false; ScopeConfirmed = false; UseCarry = value == "FUTU";
        OnPropertyChanged(nameof(IsIbkr)); OnPropertyChanged(nameof(IsFutu)); Invalidate(); RefreshCommands();
    }
    partial void OnUseCarryChanged(bool value) { openingPath = null; OpeningLabel = "尚未选择期初资料"; Invalidate(); }
    partial void OnOpeningZeroChanged(bool value) => Invalidate();
    partial void OnScopeConfirmedChanged(bool value) => Invalidate();
    partial void OnSaveCanonicalSnapshotChanged(bool value) => Invalidate();
    /// <summary>预填完整自然年日期。</summary>
    private void SetDates() { if (Year is >= 2000 and <= 2100) { From = new(Year, 1, 1); To = new(Year, 12, 31); } }
    private void Invalidate() { State.Clear(); State.Status = "输入已更新，请重新计算。"; RefreshCommands(); }
    private bool CanOperate() => State.IsIdle;
    private bool CanCalculate() => State.IsIdle && SelectedFiles.Length > 0;
    private bool CanFutuOperate() => State.IsIdle && IsFutu;
    private bool CanClearDividend() => CanFutuOperate() && futuDividendFile is not null;
    private bool CanClearTrades() => CanFutuOperate() && futuTradeFiles.Length > 0;
    private bool CanCancel() => State.IsBusy && cancellation is not null;
    private void RefreshCommands()
    {
        PickReportsCommand.NotifyCanExecuteChanged(); PickOpeningCommand.NotifyCanExecuteChanged();
        CalculateCommand.NotifyCanExecuteChanged(); DownloadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        PickFutuDividendCommand.NotifyCanExecuteChanged(); PickFutuTradesCommand.NotifyCanExecuteChanged();
        ClearFutuDividendCommand.NotifyCanExecuteChanged(); ClearFutuTradesCommand.NotifyCanExecuteChanged();
    }
    /// <summary>选择对应券商的本地文件。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void PickReports()
    {
        var selected = State.Interaction.PickFiles(IsIbkr ? "Activity Flex XML|*.xml" : "富途年度收入与交易|*.xlsx;*.pdf", true);
        if (selected.Length == 0) return;
        files = selected; ReportsLabel = string.Join("；", selected.Select(System.IO.Path.GetFileName)); Invalidate();
    }
    /// <summary>重新选择只替换收入主表；取消选择保持原文件。</summary>
    [RelayCommand(CanExecute = nameof(CanFutuOperate))]
    private void PickFutuDividend()
    {
        var selected = State.Interaction.PickFiles("富途股息收入|*.xlsx;*.pdf", false);
        if (selected.Length == 0) return;
        if (futuTradeFiles.Contains(selected[0], StringComparer.OrdinalIgnoreCase)) { State.Interaction.ShowError("股息收入与交易明细请选择不同文件。"); return; }
        futuDividendFile = selected[0]; FutuDividendLabel = System.IO.Path.GetFileName(selected[0]); Invalidate();
    }
    /// <summary>替换交易文件集合，不影响已选择的收入文件。</summary>
    [RelayCommand(CanExecute = nameof(CanFutuOperate))]
    private void PickFutuTrades()
    {
        var selected = State.Interaction.PickFiles("富途交易明细|*.xlsx", true);
        if (selected.Length == 0) return;
        if (futuDividendFile is not null && selected.Contains(futuDividendFile, StringComparer.OrdinalIgnoreCase))
        { State.Interaction.ShowError("股息收入与交易明细请选择不同文件。"); return; }
        futuTradeFiles = selected; FutuTradesLabel = string.Join("；", selected.Select(System.IO.Path.GetFileName)); Invalidate();
    }
    [RelayCommand(CanExecute = nameof(CanClearDividend))]
    private void ClearFutuDividend() { futuDividendFile = null; FutuDividendLabel = "尚未选择股息收入文件"; Invalidate(); }
    [RelayCommand(CanExecute = nameof(CanClearTrades))]
    private void ClearFutuTrades() { futuTradeFiles = []; FutuTradesLabel = "尚未选择交易明细文件"; Invalidate(); }
    /// <summary>选择期初成本，取消不改变现有资料。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void PickOpening()
    {
        if (!IsIbkr && !UseCarry) { State.Interaction.ShowError("富途期初请选择 C# LOT 结转，或提供完整历史流水。"); return; }
        var selected = State.Interaction.PickFiles(UseCarry ? "C# LOT 结转|*.json" : "期初 LOT XML|*.xml", false);
        if (selected.Length == 0) return;
        openingPath = selected[0]; OpeningLabel = System.IO.Path.GetFileName(openingPath); OpeningZero = false; Invalidate();
    }
    /// <summary>提交输入快照，成功后刷新历史并导航至税务结果。</summary>
    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private async Task CalculateAsync()
    {
        var request = new DesktopCalculationRequest(SelectedFiles.ToArray(), Year, Broker, OpeningZero, ScopeConfirmed, UseCarry, openingPath, SaveCanonicalSnapshot);
        Invalidate();
        using var cts = new CancellationTokenSource(); cancellation = cts;
        try
        {
            var success = await State.RunAsync(async () =>
            {
                State.Status = "正在导入、匹配 FIFO 并核对年度资料……";
                var outcome = await State.UseCases.CalculateAsync(request, cts.Token);
                await State.DisplayAsync(outcome.Result, outcome.RatesFingerprint);
            });
            if (success) calculated();
        }
        finally { cancellation = null; CancelCommand.NotifyCanExecuteChanged(); }
    }
    /// <summary>请求后台下载并把保存文件选为本次输入。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task DownloadAsync()
    {
        if (!IsIbkr || From is null || To is null) { State.Interaction.ShowError("请选择 IBKR 和下载起止日期。"); return; }
        var path = State.Interaction.SaveFile("Activity Flex XML|*.xml", $"IBKR_{From:yyyyMMdd}_{To:yyyyMMdd}.xml");
        if (path is null) return;
        using var cts = new CancellationTokenSource(); cancellation = cts;
        try
        {
            await State.RunAsync(async () =>
            {
                State.Status = "正在请求 IBKR 报告……";
                await State.UseCases.DownloadAsync(QueryId, Token, DateOnly.FromDateTime(From.Value), DateOnly.FromDateTime(To.Value), path, cts.Token);
                files = [path]; ReportsLabel = System.IO.Path.GetFileName(path); ScopeConfirmed = false; Invalidate();
                State.Status = "报告下载完成，请核对范围后开始计算。";
            });
        }
        finally { cancellation = null; CancelCommand.NotifyCanExecuteChanged(); }
    }
    /// <summary>取消当前后台请求。</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => cancellation?.Cancel();
}
