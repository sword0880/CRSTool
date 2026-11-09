using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CRS.Application;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages;

internal static class DashboardInteractionChecks
{
    internal static async Task RunAsync()
    {
        var source = Create("FUTU", 2023, 0);
        var old = Create("IBKR", 2022, null);
        var annual = new CalculationResult
        {
            SnapshotId = "ANNUAL", Year = 2023, Broker = "MULTI", Summary = new(0, 0, 0, 0, 0, 0, 0),
            ImportedTradeCount = 90, Issues = [new("DEMO", "合成")],
            AggregationSources = [new("FUTU", "FUTU", 2023, ["A1"]), new("IBKR-2023", "IBKR", 2023, ["A2"])]
        };
        var useCases = new DashboardCases(old, source, annual);
        useCases.Rows = [Row(old), Row(source), .. Enumerable.Repeat(Row(source), 98), Row(annual)];
        var preference = new OverviewPreference();
        var shell = new ShellViewModel(useCases, new SilentInteraction(), preference);
        string? route = null;
        shell.NavigateRequested += page => route = page;

        await shell.Dashboard.RefreshAsync();
        Check(shell.Dashboard.SelectedYear == 2023 && shell.Dashboard.Result == annual
            && shell.Dashboard.ScopeTitle == "2023 年度汇总" && shell.Dashboard.AccountScope.Contains("A1")
            && shell.Dashboard.TradeCount == "90" && shell.Dashboard.IssueCount == "1", "默认最新有数据年度，跨页优先年度汇总");
        Check(!shell.State.HasResult && shell.Dashboard.RecentTasks[0].Year == 2022, "仅预览核算结果，最近任务跨年度");
        await shell.Dashboard.OpenDetailCommand.ExecuteAsync("review");
        Check(route == "review" && shell.State.Result == annual, "明细打开同一份冻结结果");

        var pending = new TaskCompletionSource<CalculationResult>();
        useCases.PendingRead = pending.Task;
        var olderRead = shell.Dashboard.SelectYearAsync(2022);
        await shell.Dashboard.SelectYearAsync(2024);
        pending.SetResult(old);
        await olderRead;
        Check(shell.Dashboard.ShowEmpty && shell.Dashboard.SelectedYear == 2024 && shell.Dashboard.Result is null
            && shell.Dashboard.EmptyMessage.Contains("2024") && shell.State.Result == annual, "迟到的旧结果不覆盖空年份，也不清掉工作区");
        shell.Dashboard.OpenImportCommand.Execute(null);
        Check(route == "import" && shell.Import.Year == 2024, "空年份导入预填年度");

        await shell.Dashboard.OpenTaskCommand.ExecuteAsync(shell.Dashboard.RecentTasks[0]);
        Check(route == "tax" && shell.State.Result == old && shell.Dashboard.SelectedYear == 2024, "打开跨年度最近任务不改变概览筛选");
        await shell.Dashboard.SelectYearAsync(2022);
        Check(shell.Dashboard.TradeCount == "未登记" && shell.Dashboard.ScopeTitle.Contains("盈透证券"), "旧快照缺失笔数保留未知状态");
        var reopened = new ShellViewModel(useCases, new SilentInteraction(), preference);
        await reopened.Dashboard.RefreshAsync();
        Check(reopened.Dashboard.SelectedYear == 2022, "重新进入后恢复仍有结果的上次年度");
        useCases.Rows = [Row(source)];
        reopened = new ShellViewModel(useCases, new SilentInteraction(), preference);
        await reopened.Dashboard.RefreshAsync();
        Check(reopened.Dashboard.SelectedYear == 2023 && reopened.Dashboard.TradeCount == "0", "上次年度已无结果时回退，保留真实零笔");
        useCases.FailReads = true;
        await reopened.Dashboard.RefreshAsync();
        Check(reopened.Dashboard.LoadFailed && !reopened.Dashboard.ShowEmpty, "读取失败和无数据分开");
        useCases.FailReads = false;
        await reopened.Dashboard.RefreshCommand.ExecuteAsync(null);
        Check(reopened.Dashboard.HasResult, "可以重试读取");
        var first = new ShellViewModel(new DashboardCases(), new SilentInteraction());
        await first.Dashboard.RefreshAsync();
        Check(first.Dashboard.ShowEmpty && first.Dashboard.EmptyMessage.Contains("还没有导入")
            && first.Dashboard.OpenImportCommand.CanExecute(null), "首次使用有导入入口");
        Console.WriteLine("概览模型通过：年度恢复、汇总优先、明细范围、快速切换、空状态与重试。");
    }

    internal static void CheckPage()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                foreach (var name in new[] { "BrandPalette", "SharedStyles" })
                    app.Resources.MergedDictionaries.Add(new() { Source = new($"pack://application:,,,/CRS.Desktop.Wpf;component/Styles/{name}.xaml") });
                var source = Create("FUTU", 2023, 90);
                var cases = new DashboardCases(source) { Rows = [Row(source)] };
                var shell = new ShellViewModel(cases, new SilentInteraction());
                var page = new DashboardPage(shell);
                var host = new Window { Content = page, Width = 900, Height = 720 };
                host.Show();
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var selector = (ComboBox)page.FindName("YearSelector");
                Check(selector.SelectedItem is 2023 && shell.Dashboard.HasResult, "真实页面首次加载默认年度");
                selector.SetCurrentValue(ComboBox.SelectedItemProperty, 2024);
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(shell.Dashboard.SelectedYear == 2024 && shell.Dashboard.ShowEmpty, "真实下拉框触发空年度");
                selector.SetCurrentValue(ComboBox.SelectedItemProperty, 2023);
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(shell.Dashboard.TradeCount == "90", "真实下拉框可切回已有结果");
                host.Close();
            }
            catch (Exception error) { failure = error; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("概览 WPF 页面通过：加载、选择空年度和切回有数据年度。");
    }

    private static CalculationResult Create(string broker, int year, int? trades) => new()
    {
        SnapshotId = broker, Broker = broker, Year = year, ImportedTradeCount = trades,
        CoveredAccounts = ["TEST"], Summary = new(0, 0, 0, 0, 0, 0, 0)
    };

    private static HistoryItem Row(CalculationResult result) => new(result.SnapshotId, result.Year, "2025-06-01T00:00:00Z",
        false, result.Issues.Count, result.Broker, IsAnnualAggregate: result.IsAnnualAggregate);

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class DashboardCases(params CalculationResult[] results) : FakeUseCases
    {
        public HistoryItem[] Rows { get; set; } = [];
        public Task<CalculationResult>? PendingRead { get; set; }
        public bool FailReads { get; set; }

        public override Task<HistoryPageResult> QueryHistoryAsync(int pageNumber, int pageSize)
            => Task.FromResult(new HistoryPageResult(Rows.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList(),
                Rows.Length, pageNumber, pageSize));

        public override Task<CalculationResult> LoadAsync(string id)
        {
            if (FailReads) return Task.FromException<CalculationResult>(new InvalidOperationException("合成读取失败"));
            var pending = PendingRead;
            PendingRead = null;
            return pending ?? Task.FromResult(results.Single(result => result.SnapshotId == id));
        }
    }

    private sealed class OverviewPreference : IDesktopSettingsService
    {
        private int? year;
        public DesktopSettings Load() => new("unused", "unused");
        public Task SaveAsync(DesktopSettings settings) => Task.CompletedTask;
        public int? LoadOverviewTaxYear() => year;
        public void SaveOverviewTaxYear(int value) => year = value;
    }
}
