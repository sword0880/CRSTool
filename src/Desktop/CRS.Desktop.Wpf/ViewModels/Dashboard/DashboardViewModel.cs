using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;

namespace CRS.DesktopClient.ViewModels;

/// <summary>选择已保存的年度结果，并同步工作区的核算页面。</summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly IDesktopSettingsService? settings;
    private readonly Action<string> navigate;
    private readonly Action<int> openImport;
    private readonly CancellationTokenSource lifetime = new();
    private readonly int currentYear = CRS.Timing.BeijingClock.Today.Year;
    private Dictionary<int, HistoryItem> preferredByYear = [];
    private bool preferenceLoaded;
    private int? chosenYear;
    private int readVersion;

    public WorkspaceState State { get; }
    public PerformanceViewModel Performance { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EmptyMessage), nameof(ImportLabel))]
    private IReadOnlyList<HistoryItem> recentTasks = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSelectYear))]
    private IReadOnlyList<int> years = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSelectYear))]
    private bool isReadingIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(EmptyMessage), nameof(ImportLabel))]
    private int? selectedYear;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmpty)), NotifyCanExecuteChangedFor(nameof(OpenImportCommand))]
    private bool isReading = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmpty)), NotifyCanExecuteChangedFor(nameof(OpenImportCommand))]
    private bool loadFailed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CoverageToggleLabel))]
    private bool isCoverageExpanded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsViewingHistory))]
    private HistoryItem? openedHistoryTask;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ShowEmpty), nameof(ScopeTitle), nameof(BrokerCoverage),
        nameof(HasAccountCoverage), nameof(CoverageSummary),
        nameof(TradeCount), nameof(IssueCount), nameof(MatchCount), nameof(TaxStatus), nameof(TaxHint))]
    [NotifyCanExecuteChangedFor(nameof(OpenDetailCommand), nameof(OpenImportCommand))]
    private CalculationResult? result;

    public bool HasResult => Result is not null;
    public bool IsViewingHistory => OpenedHistoryTask is not null;
    public bool CanSelectYear => State.IsIdle && !IsReadingIndex && Years.Count > 0;
    public bool ShowEmpty => !IsReading && !LoadFailed && !HasResult && SelectedYear.HasValue;
    public string EmptyMessage => RecentTasks.Count == 0 && chosenYear is null
        ? "还没有导入年度资料。"
        : $"{SelectedYear} 年暂无核算结果。";
    public string ImportLabel => RecentTasks.Count == 0 && chosenYear is null
        ? "导入年度报表"
        : $"导入 {SelectedYear} 年报表";
    public string ScopeTitle => Result is not { } value ? "" : value.IsAnnualAggregate
        ? $"{value.Year} 年度汇总"
        : $"{value.Year} 年 · {PerformanceViewModel.BrokerName(value.Broker)}";
    public IReadOnlyList<DashboardBrokerCoverage> BrokerCoverage { get; private set; } = [];
    public bool HasAccountCoverage => BrokerCoverage.Count > 0;
    public string CoverageSummary => HasAccountCoverage
        ? $"覆盖 {BrokerCoverage.Sum(x => x.Count):N0} 个账户 · {string.Join(" · ", BrokerCoverage.Select(x => $"{x.Broker} {x.Count:N0}"))}"
        : "覆盖账户未登记";
    public string CoverageToggleLabel => IsCoverageExpanded ? "收起明细" : "查看明细";

    partial void OnResultChanged(CalculationResult? value)
    {
        IsCoverageExpanded = false;
        if (value is null)
        {
            BrokerCoverage = [];
            return;
        }
        // 年度结果保留来源券商关系，跨券商的同名账号分开计数。
        var accounts = value.IsAnnualAggregate
            ? value.AggregationSources.SelectMany(source => source.Accounts.Select(account => (source.Broker, Account: account)))
            : value.CoveredAccounts.Select(account => (value.Broker, Account: account));
        BrokerCoverage = accounts.Where(x => !string.IsNullOrWhiteSpace(x.Account))
            .GroupBy(x => x.Broker, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var ids = group.Select(x => x.Account).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new DashboardBrokerCoverage(PerformanceViewModel.BrokerName(group.Key), ids.Length, string.Join(" · ", ids));
            }).ToArray();
    }

    public string TradeCount => Result is { } value ? value.ImportedTradeCount?.ToString("N0") ?? "未登记" : "—";
    public string IssueCount => Result?.Issues.Count.ToString("N0") ?? "—";
    public string MatchCount => Result?.Matches.Count.ToString("N0") ?? "—";
    public string TaxStatus => Result is { } value ? ResultLabels.Calculation(value.CalculationStatus) : "";
    public string TaxHint => Result is { } value ? ResultLabels.Usage(value) : "";

    public DashboardViewModel(WorkspaceState state, PerformanceViewModel performance, IDesktopSettingsService? settings,
        Action<string> navigate, Action<int> openImport)
    {
        State = state;
        Performance = performance;
        this.settings = settings;
        this.navigate = navigate;
        this.openImport = openImport;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(state.IsBusy)) return;
            OnPropertyChanged(nameof(CanSelectYear));
            OpenDetailCommand.NotifyCanExecuteChanged();
            OpenTaskCommand.NotifyCanExecuteChanged();
            OpenImportCommand.NotifyCanExecuteChanged();
        };
    }

    [RelayCommand]
    private void Navigate(string page) => navigate(page);

    [RelayCommand]
    private void ToggleCoverage() => IsCoverageExpanded = HasAccountCoverage && !IsCoverageExpanded;

    [RelayCommand]
    private void OpenAccountOverview()
    {
        Performance.ShowAccountOverview = true;
        navigate("performance");
    }

    internal void ShowOpenedHistory(HistoryItem task, CalculationResult opened)
    {
        if (lifetime.IsCancellationRequested) return;
        var reloadIndex = IsReadingIndex;
        readVersion++;
        OpenedHistoryTask = task;
        if (!Years.Contains(task.Year)) Years = Years.Append(task.Year).OrderDescending().ToArray();
        SelectedYear = task.Year;
        Result = opened;
        LoadFailed = false;
        IsReading = false;
        IsReadingIndex = false;
        if (reloadIndex) _ = RefreshAsync();
    }

    [RelayCommand]
    private Task ReturnToYearOverviewAsync()
    {
        if (OpenedHistoryTask is not { } task || lifetime.IsCancellationRequested || !State.IsIdle) return Task.CompletedTask;
        chosenYear = task.Year;
        OpenedHistoryTask = null;
        return RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        // 刷新任务列表时保留从导入历史明确打开的结果。
        var indexOnly = IsViewingHistory;
        var version = indexOnly ? ++readVersion : BeginRead();
        var workspaceVersion = State.ResultVersion;
        IsReadingIndex = true;
        try
        {
            const int pageSize = 100;
            var preferred = new Dictionary<int, HistoryItem>();
            var recent = Array.Empty<HistoryItem>();
            for (var page = 1; ; page++)
            {
                var history = await State.UseCases.QueryHistoryAsync(page, pageSize);
                if (version != readVersion) return;
                if (page == 1) recent = history.Rows.Take(5).ToArray();
                foreach (var task in history.Rows)
                {
                    // 历史按创建时间倒序。每年优先取最新汇总，否则取最新单份结果。
                    if (!preferred.TryGetValue(task.Year, out var saved) || task.IsAnnualAggregate && !saved.IsAnnualAggregate)
                        preferred[task.Year] = task;
                }
                if (history.PageNumber != page || (long)page * pageSize >= history.TotalCount) break;
            }
            preferredByYear = preferred;
            RecentTasks = recent;
            var options = preferred.Keys.Concat(Enumerable.Range(2000, currentYear - 2000 + 1))
                .Concat(OpenedHistoryTask is { } opened ? [opened.Year] : [])
                .Distinct().OrderDescending().ToArray();
            if (!Years.SequenceEqual(options)) Years = options;
            if (!preferenceLoaded)
            {
                var savedYear = ReadPreference();
                if (chosenYear is null && savedYear is int year && preferred.ContainsKey(year)) chosenYear = year;
                preferenceLoaded = true;
            }
            if (indexOnly) return;
            await State.WaitForIdleAsync(lifetime.Token);
            if (version != readVersion) return;
            SelectedYear = chosenYear ?? (preferred.Count > 0 ? preferred.Keys.Max() : currentYear - 1);
            IsReadingIndex = false;
            await LoadSelectedResultAsync(version, workspaceVersion);
        }
        catch (Exception error) { ReportReadFailure(version, error); }
        finally
        {
            if (version == readVersion)
            {
                IsReadingIndex = false;
                IsReading = false;
            }
        }
    }

    public async Task SelectYearAsync(int year)
    {
        if (lifetime.IsCancellationRequested || !CanSelectYear || year == SelectedYear || !Years.Contains(year)) return;
        OpenedHistoryTask = null;
        var version = BeginRead();
        chosenYear = year;
        SelectedYear = year;
        try { settings?.SaveOverviewTaxYear(year); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { State.UseCases.LogFailure(error.GetType().Name); }
        try { await LoadSelectedResultAsync(version, State.ResultVersion); }
        catch (Exception error) { ReportReadFailure(version, error); }
        finally { if (version == readVersion) IsReading = false; }
    }

    private int? ReadPreference()
    {
        try { return settings?.LoadOverviewTaxYear(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            State.UseCases.LogFailure(error.GetType().Name);
            return null;
        }
    }

    private int BeginRead()
    {
        IsReading = true;
        LoadFailed = false;
        Result = null;
        return ++readVersion;
    }

    private async Task LoadSelectedResultAsync(int version, int workspaceVersion)
    {
        bool IsCurrent() => version == readVersion && !lifetime.IsCancellationRequested
            && State.IsIdle && State.ResultVersion == workspaceVersion;

        if (SelectedYear is not int year) return;
        if (!IsCurrent()) { if (version == readVersion) LoadFailed = true; return; }

        State.Year = year;
        if (!preferredByYear.TryGetValue(year, out var task))
        {
            if (State.HasResult) State.Clear();
            State.Status = EmptyMessage;
            return;
        }
        if (State.Result is { } active && active.Year != year)
        {
            State.Clear();
            workspaceVersion = State.ResultVersion;
        }
        if (!State.HasResult) State.Status = $"正在打开 {year} 年核算结果……";
        var saved = await ReadResultAsync(task.Id, year);
        var displayed = IsCurrent() && (ReferenceEquals(State.Result, saved)
            || await State.DisplayAsync(saved, cancellation: lifetime.Token, isCurrent: IsCurrent));
        if (version != readVersion) return;
        if (displayed) Result = saved;
        else LoadFailed = true;
    }

    private async Task<CalculationResult> ReadResultAsync(string id, int year)
    {
        var saved = State.Result is { } active && active.SnapshotId == id ? active : await State.UseCases.LoadAsync(id);
        if (saved.Year != year || saved.SnapshotId != id) throw new CrsException("核算结果与所选任务不一致。");
        return saved;
    }

    private void ReportReadFailure(int version, Exception error)
    {
        if (version != readVersion) return;
        State.UseCases.LogFailure(error.GetType().Name);
        if (!IsViewingHistory)
        {
            LoadFailed = true;
            if (State.IsIdle && !State.HasResult && SelectedYear is int year)
                State.Status = $"暂时无法读取 {year} 年核算结果。";
        }
    }

    private bool CanOpenDetail() => State.IsIdle && HasResult && !lifetime.IsCancellationRequested;
    private bool CanOpenTask() => State.IsIdle && !lifetime.IsCancellationRequested;
    private bool CanOpenImport() => State.IsIdle && ShowEmpty && !lifetime.IsCancellationRequested;

    [RelayCommand(CanExecute = nameof(CanOpenImport))]
    private void OpenImport() => openImport(SelectedYear!.Value);

    [RelayCommand(CanExecute = nameof(CanOpenDetail))]
    private async Task OpenDetailAsync(string page)
    {
        if (Result is { } selected && await OpenResultAsync(selected.SnapshotId, selected.Year) is not null) navigate(page);
    }

    [RelayCommand(CanExecute = nameof(CanOpenTask))]
    private async Task OpenTaskAsync(HistoryItem task)
    {
        if (await OpenResultAsync(task.Id, task.Year) is not { } opened) return;
        ShowOpenedHistory(task, opened);
        navigate("tax");
    }

    private async Task<CalculationResult?> OpenResultAsync(string id, int year)
    {
        var opened = await State.RunAsync(async () =>
        {
            if (State.Result?.SnapshotId == id) return;
            var selected = Result is { } summary && summary.SnapshotId == id ? summary : await ReadResultAsync(id, year);
            lifetime.Token.ThrowIfCancellationRequested();
            await State.DisplayAsync(selected, cancellation: lifetime.Token);
        });
        return opened && !lifetime.IsCancellationRequested && State.Result is { } result && result.SnapshotId == id ? result : null;
    }

    public void ClearForLock()
    {
        lifetime.Cancel();
        readVersion++;
        OpenedHistoryTask = null;
        Result = null;
        RecentTasks = [];
        preferredByYear.Clear();
        SelectedYear = null;
        Years = [];
    }
}

public sealed record DashboardBrokerCoverage(string Broker, int Count, string Accounts);
