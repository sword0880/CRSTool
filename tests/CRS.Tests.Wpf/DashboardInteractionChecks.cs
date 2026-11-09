using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
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
        var annual = Annual();
        var useCases = new DashboardCases(old, source, annual);
        useCases.Rows = [Row(old), Row(source), .. Enumerable.Repeat(Row(source), 98), Row(annual)];
        var preference = new OverviewPreference();
        var shell = new ShellViewModel(useCases, new SilentInteraction(), preference);
        string? route = null;
        shell.NavigateRequested += page => route = page;

        await shell.Dashboard.RefreshAsync();
        Check(shell.Dashboard.SelectedYear == 2023 && shell.Dashboard.Result == annual
            && shell.Dashboard.ScopeTitle == "2023 年度汇总"
            && shell.Dashboard.TradeCount == "90" && shell.Dashboard.IssueCount == "1", "默认最新有数据年度，跨页优先年度汇总");
        Check(!shell.State.HasResult && shell.Dashboard.RecentTasks[0].Year == 2022, "仅预览核算结果，最近任务跨年度");
        Check(shell.Dashboard.CoverageSummary == "覆盖 5 个账户 · 富途证券 3 · 盈透证券（IBKR） 2"
            && shell.Dashboard.BrokerCoverage[0].Accounts == "DEMO-1001 · F-2302 · F-6209"
            && shell.Dashboard.BrokerCoverage[1].Accounts == "DEMO-1001 · U4010923", "按券商统计并对同券商重复账户去重");
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
        first.Dashboard.Result = new() { Year = 2023, Summary = new(0, 0, 0, 0, 0, 0, 0) };
        Check(!first.Dashboard.HasAccountCoverage && first.Dashboard.CoverageSummary == "覆盖账户未登记", "旧结果缺失范围时显示未登记");
        await CheckHistoryLinkAsync();
        Console.WriteLine("概览模型通过：历史联动、年度恢复、汇总优先、明细范围、快速切换、空状态与重试。");
    }

    private static async Task CheckHistoryLinkAsync()
    {
        var annual = Annual();
        var historical = Create("FUTU", 2023, 16);
        var archived = Create("IBKR", 2022, 7);
        var cases = new DashboardCases(annual, historical, archived)
        {
            Rows = [Row(annual), Row(historical)],
            ArchivedRows = [Row(archived) with { IsArchived = true }]
        };
        var preference = new OverviewPreference();
        preference.SaveOverviewTaxYear(2023);
        var shell = new ShellViewModel(cases, new SilentInteraction(), preference);
        var dashboard = shell.Dashboard;
        await dashboard.RefreshAsync();
        await shell.History.RefreshCommand.ExecuteAsync(null);
        shell.History.Selected = shell.History.Rows.Single(row => row.Id == historical.SnapshotId);
        var pending = new TaskCompletionSource<CalculationResult>();
        cases.PendingRead = pending.Task;
        var delayedDefault = dashboard.RefreshAsync();
        await shell.History.OpenCommand.ExecuteAsync(null);
        pending.SetResult(annual);
        await delayedDefault;
        await dashboard.RefreshAsync();
        Check(dashboard.Result == historical && dashboard.IsViewingHistory && dashboard.TradeCount == "16"
            && dashboard.SelectedYear == 2023, "同年打开的单份历史结果不被年度汇总或迟到的读取覆盖");
        await shell.State.DisplayAsync(annual);
        await dashboard.OpenDetailCommand.ExecuteAsync("trades");
        Check(shell.State.Result == historical, "从概览点进明细仍使用历史记录的快照");
        await dashboard.ReturnToYearOverviewCommand.ExecuteAsync(null);
        Check(dashboard.Result == annual && !dashboard.IsViewingHistory, "同年度可明确切回默认汇总");
        await shell.History.ToggleArchiveViewCommand.ExecuteAsync(null);
        shell.History.Selected = shell.History.Rows.Single();
        await shell.History.OpenCommand.ExecuteAsync(null);
        await dashboard.RefreshAsync();
        Check(dashboard.Result == archived && dashboard.OpenedHistoryTask?.IsArchived == true
            && dashboard.SelectedYear == 2022 && preference.LoadOverviewTaxYear() == 2023, "跨年打开归档记录切换概览年份，不改启动偏好");
        await dashboard.SelectYearAsync(2023);
        Check(dashboard.Result == annual && !dashboard.IsViewingHistory, "手动换年恢复年度概览模式");
    }

    internal static void CheckPage(string? previewDirectory = null)
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
                var source = Annual();
                var historical = Create("FUTU", 2023, 16);
                var cases = new DashboardCases(source, historical) { Rows = [Row(source), Row(historical)] };
                var performance = new PerformanceInteractionChecks.Cases
                {
                    Data = new()
                    {
                        Accounts = Enumerable.Range(1, 7).Select(i => new CRS.Domain.Performance.InvestmentAccount(
                            $"DEMO-{i}", i <= 4 ? "FUTU" : "IBKR", $"合成账户 {i}", "USD", new(2023, 1, 1))).ToArray()
                    }
                };
                var shell = new ShellViewModel(cases, new SilentInteraction(), performance: performance);
                var page = new DashboardPage(shell) { Background = (Brush)app.FindResource("BrandCanvasBrush") };
                var host = new Window
                {
                    Content = page, Width = 900, Height = 760,
                    Background = (Brush)app.FindResource("BrandCanvasBrush"), FontFamily = new("Microsoft YaHei UI"), FontSize = 14
                };
                host.Show();
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var selector = (ComboBox)page.FindName("YearSelector");
                Check(selector.SelectedItem is 2023 && shell.Dashboard.HasResult, "真实页面首次加载默认年度");
                var coverage = (Button)page.FindName("AccountCoverageToggle");
                var rows = (ItemsControl)page.FindName("BrokerCoverageRows");
                Check(coverage.IsVisible && Equals(coverage.Content, "查看明细") && !rows.IsVisible
                    && shell.Performance.AccountBrokerSummary.StartsWith("共 7 个账户"), "年度范围默认折叠，全局总览独立计数");
                if (previewDirectory is not null) Capture(page, previewDirectory, "collapsed");
                coverage.Command.Execute(null);
                host.UpdateLayout();
                Check(Equals(coverage.Content, "收起明细") && rows.IsVisible && rows.Items.Count == 2, "展开后显示两组券商账户");
                if (previewDirectory is not null) Capture(page, previewDirectory, "expanded");
                coverage.Command.Execute(null);
                Check(!rows.IsVisible && Equals(coverage.Content, "查看明细"), "可收起账号明细");
                coverage.Command.Execute(null);
                selector.SetCurrentValue(ComboBox.SelectedItemProperty, 2024);
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(shell.Dashboard.SelectedYear == 2024 && shell.Dashboard.ShowEmpty, "真实下拉框触发空年度");
                selector.SetCurrentValue(ComboBox.SelectedItemProperty, 2023);
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(shell.Dashboard.TradeCount == "90" && !rows.IsVisible, "切回有数据年度，覆盖账户恢复折叠");
                var historyPage = new HistoryPage(shell);
                host.Content = historyPage;
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                shell.History.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                shell.History.Selected = shell.History.Rows.Single(row => row.Id == historical.SnapshotId);
                var open = (Button)historyPage.FindName("OpenSelectedResultButton");
                ((IAsyncRelayCommand)open.Command).ExecuteAsync(null).GetAwaiter().GetResult();
                host.Content = page;
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var yearOverview = (Button)page.FindName("ReturnToYearOverviewButton");
                Check(yearOverview.IsVisible && shell.Dashboard.TradeCount == "16", "从导入历史打开后，实际概览显示该记录");
                ((IAsyncRelayCommand)yearOverview.Command).ExecuteAsync(null).GetAwaiter().GetResult();
                Check(!yearOverview.IsVisible && shell.Dashboard.TradeCount == "90", "实际页面可切回本年度概览");
                host.Close();
            }
            catch (Exception error) { failure = error; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("概览 WPF 页面通过：历史记录联动、恢复年度概览、年度切换和覆盖明细。");
    }

    private static void Capture(FrameworkElement page, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static CalculationResult Annual() => new()
    {
        SnapshotId = "ANNUAL", Year = 2023, Broker = "MULTI", Summary = new(0, 0, 0, 0, 0, 0, 0),
        ImportedTradeCount = 90, Issues = [new("DEMO", "合成")],
        AggregationSources =
        [
            new("FUTU", "FUTU", 2023, ["F-2302", "DEMO-1001", "F-6209", "F-2302"]),
            new("IBKR-2023", "IBKR", 2023, ["U4010923", "DEMO-1001"])
        ]
    };

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
        public HistoryItem[] ArchivedRows { get; init; } = [];
        public Task<CalculationResult>? PendingRead { get; set; }
        public bool FailReads { get; set; }

        public override Task<HistoryPageResult> QueryHistoryAsync(int pageNumber, int pageSize)
            => Task.FromResult(new HistoryPageResult(Rows.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList(),
                Rows.Length, pageNumber, pageSize));
        public override Task<HistoryPageResult> QueryArchivedHistoryAsync(int pageNumber, int pageSize)
            => Task.FromResult(new HistoryPageResult(ArchivedRows.ToList(), ArchivedRows.Length, pageNumber, pageSize));

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
