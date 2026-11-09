using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;

namespace CRS.DesktopClient.ViewModels;

/// <summary>独立浏览已保存的年度结果，打开明细时才切换工作区的核算结果。</summary>
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ShowEmpty), nameof(ScopeTitle), nameof(AccountScope),
        nameof(TradeCount), nameof(IssueCount), nameof(MatchCount), nameof(TaxStatus), nameof(TaxHint))]
    [NotifyCanExecuteChangedFor(nameof(OpenDetailCommand), nameof(OpenImportCommand))]
    private CalculationResult? result;

    public bool HasResult => Result is not null;
    public bool CanSelectYear => !IsReadingIndex && Years.Count > 0;
    public bool ShowEmpty => !IsReading && !LoadFailed && !HasResult && SelectedYear.HasValue;
    public string EmptyMessage => RecentTasks.Count == 0 && chosenYear is null
        ? "还没有导入年度资料。"
        : $"{SelectedYear} 年暂无核算结果。";
    public string ImportLabel => RecentTasks.Count == 0 && chosenYear is null
        ? "导入年度报表"
        : $"导入 {SelectedYear} 年报表";
    public string ScopeTitle => Result is not { } value ? "" : value.IsAnnualAggregate
        ? $"{value.Year} 年度汇总"
        : $"{value.Year} 年 · {BrokerName(value.Broker)}";
    public string AccountScope
    {
        get
        {
            if (Result is not { } value) return "";
            var accounts = value.IsAnnualAggregate
                ? value.AggregationSources.SelectMany(s => s.Accounts.Select(a => $"{BrokerName(s.Broker)} / {a}"))
                : value.CoveredAccounts.AsEnumerable();
            var scope = string.Join("、", accounts.Distinct());
            return scope.Length == 0 ? "账户范围未登记" : $"覆盖账户 · {scope}";
        }
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
            OpenDetailCommand.NotifyCanExecuteChanged();
            OpenTaskCommand.NotifyCanExecuteChanged();
            OpenImportCommand.NotifyCanExecuteChanged();
        };
    }

    [RelayCommand]
    private void Navigate(string page) => navigate(page);

    [RelayCommand]
    private void OpenAccountOverview()
    {
        Performance.ShowAccountOverview = true;
        navigate("performance");
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        var version = BeginRead();
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
                .Distinct().OrderDescending().ToArray();
            if (!Years.SequenceEqual(options)) Years = options;
            if (!preferenceLoaded)
            {
                var savedYear = ReadPreference();
                if (savedYear is int year && preferred.ContainsKey(year)) chosenYear = year;
                preferenceLoaded = true;
            }
            SelectedYear = chosenYear ?? (preferred.Count > 0 ? preferred.Keys.Max() : currentYear - 1);
            IsReadingIndex = false;
            await LoadSelectedResultAsync(version);
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
        var version = BeginRead();
        chosenYear = year;
        SelectedYear = year;
        try { settings?.SaveOverviewTaxYear(year); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { State.UseCases.LogFailure(error.GetType().Name); }
        try { await LoadSelectedResultAsync(version); }
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

    private async Task LoadSelectedResultAsync(int version)
    {
        if (SelectedYear is not int year || !preferredByYear.TryGetValue(year, out var task)) return;
        var saved = await ReadResultAsync(task.Id, year);
        if (version == readVersion) Result = saved;
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
        LoadFailed = true;
    }

    private static string BrokerName(string broker) => broker switch
    {
        "FUTU" => "富途证券",
        "IBKR" => "盈透证券（IBKR）",
        _ => broker
    };

    private bool CanOpenDetail() => State.IsIdle && HasResult && !lifetime.IsCancellationRequested;
    private bool CanOpenTask() => State.IsIdle && !lifetime.IsCancellationRequested;
    private bool CanOpenImport() => State.IsIdle && ShowEmpty && !lifetime.IsCancellationRequested;

    [RelayCommand(CanExecute = nameof(CanOpenImport))]
    private void OpenImport() => openImport(SelectedYear!.Value);

    [RelayCommand(CanExecute = nameof(CanOpenDetail))]
    private Task OpenDetailAsync(string page) => OpenResultAsync(Result!.SnapshotId, Result.Year, page);

    [RelayCommand(CanExecute = nameof(CanOpenTask))]
    private Task OpenTaskAsync(HistoryItem task) => OpenResultAsync(task.Id, task.Year, "tax");

    private async Task OpenResultAsync(string id, int year, string page)
    {
        var opened = await State.RunAsync(async () =>
        {
            if (State.Result?.SnapshotId == id) return;
            var selected = Result is { } summary && summary.SnapshotId == id ? summary : await ReadResultAsync(id, year);
            lifetime.Token.ThrowIfCancellationRequested();
            await State.DisplayAsync(selected, cancellation: lifetime.Token);
        });
        if (opened && !lifetime.IsCancellationRequested) navigate(page);
    }

    public void ClearForLock()
    {
        lifetime.Cancel();
        readVersion++;
        Result = null;
        RecentTasks = [];
        preferredByYear.Clear();
        SelectedYear = null;
        Years = [];
    }
}
