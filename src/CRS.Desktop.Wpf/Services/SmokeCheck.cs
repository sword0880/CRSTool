using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;

namespace CRS.DesktopClient.Services;

/// <summary>隔离数据库验证页面导航、状态联动、实际绘制和后台用例。</summary>
internal static class SmokeCheck
{
    public static async Task RunAsync(MainWindow window, ShellViewModel shell, IDesktopOperations operations, string[] args,
        IVaultService? vault=null,IDesktopSettingsService? settings=null)
    {
        using var bindingErrors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            Console.Error.WriteLine("smoke: page checks started");
            var futu = args.Contains("--futu"); shell.Import.Broker = futu ? "FUTU" : "IBKR";
            var trade = new Trade(new("SYNTHETIC", "DEMO", "USD", shell.Import.Broker), "DEMO", new DateTimeOffset(2025,1,2,10,0,0,TimeSpan.Zero),
                true, "BUY", 10m, 100m, 1000m, 2m, -1002m, "DEMO-1", "synthetic.xml", 1, "STK", null);
            var result = new CalculationResult
            {
                Year = 2025, Broker = shell.Import.Broker, Summary = new(100,25,200,25,40,5,60), SnapshotId = "synthetic-wpf",
                ImportedTradeCount = 1, SessionTrades = [trade], CoveredAccounts = ["SYNTHETIC"],
                Issues = [new("SYNTHETIC", "合成界面验证，不含真实账户。")], Provisional = true,
                AnnualIncome = futu ? [new("SYNTHETIC", "合成账户", 2025, "USD", 100,25,0,"synthetic.xlsx","收入",2)] : []
            };
            operations.Repository.Save(result);
            await shell.State.DisplayAsync(result);
            if (!shell.State.HasResult || shell.Reports.ExportCarryCommand.CanExecute(null) || shell.Results.Trades.Count != 1
                || shell.Results.TaxTables.Count + shell.Results.FifoTables.Count + shell.Results.ReconciliationTables.Count != (futu ? 16 : 9))
                throw new InvalidOperationException("页面结果联动检查失败。");
            await shell.History.RefreshCommand.ExecuteAsync(null);
            shell.History.Selected = shell.History.Rows.Single(); await shell.History.OpenCommand.ExecuteAsync(null);
            if (shell.Results.Trades.Count != 0) throw new InvalidOperationException("引用型历史不应恢复未保存交易。");
            if (shell.Dashboard.RecentTasks.Count != 1 || shell.Dashboard.TradeCount != "1") throw new InvalidOperationException("概览未读取真实任务。");
            var imageIndex = Array.IndexOf(args, "--image");
            var imagePath = imageIndex >= 0 && imageIndex + 1 < args.Length ? args[imageIndex + 1] : null;
            window.IsNavigationExpanded = false;
            window.UpdateLayout(); await Task.Delay(250);
            if (window.IsNavigationExpanded) throw new InvalidOperationException("侧栏未折叠。");
            if (imagePath is not null) Capture(window, VariantPath(imagePath, "collapsed"));
            window.IsNavigationExpanded = true;
            if (shell.Settings is null) throw new InvalidOperationException("设置服务未装配。");
            var settingsWindow = new SettingsWindow(shell.Settings) { Owner = window };
            settingsWindow.Show(); settingsWindow.UpdateLayout();
            if(!shell.Settings.SecuritySettingsAvailable || !shell.Settings.PhoneVerificationEnabled
                || !((FrameworkElement)settingsWindow.FindName("PhoneVerificationPanel")).IsVisible) throw new InvalidOperationException("设置未显示当前保险库的手机验证开关。");
            shell.Settings.LogRetentionFiles = 9;
            await shell.Settings.SaveCommand.ExecuteAsync(null);
            shell.Settings.Reload();
            if (shell.Settings.LogRetentionFiles != 9 || (await useCasesForSettings()).Count != 1)
                throw new InvalidOperationException("设置未保存，或改变了当前历史数据库。");
            if (imagePath is not null) Capture(settingsWindow, VariantPath(imagePath, "settings"));
            settingsWindow.Close();
            Task<List<HistoryItem>> useCasesForSettings() => shell.State.UseCases.HistoryAsync();
            foreach (var key in MainWindow.PageTypes.Keys)
            {
                Console.Error.WriteLine("smoke: navigate " + key);
                if (!window.Navigate(key)) throw new InvalidOperationException($"导航失败：{key}");
                if (key == "rates")
                {
                    // 等导航动画结束，再验证数值调整、手动输入和未配置年度三种读取路径。
                    await Task.Delay(400);
                    window.UpdateLayout();
                    var yearInput = FindVisual<Wpf.Ui.Controls.NumberBox>(window)
                        ?? throw new InvalidOperationException("汇率年度输入框未显示。");
                    yearInput.Focus();
                    yearInput.SetCurrentValue(Wpf.Ui.Controls.NumberBox.ValueProperty, 2024d);
                    if (shell.ExchangeRates.Year != 2024)
                        throw new InvalidOperationException("修改汇率年度后未立即提交，读取会沿用旧年度。");
                    await shell.ExchangeRates.RefreshCommand.ExecuteAsync(null);
                    if (shell.ExchangeRates.Rates.Count == 0 || shell.ExchangeRates.Rates.Any(rate => rate.Year != 2024))
                        throw new InvalidOperationException("汇率读取未使用修改后的年度。");
                    var ratesPage = FindVisual<Views.Pages.ExchangeRatesPage>(window)!;
                    var readButton = (Wpf.Ui.Controls.Button)ratesPage.FindName("ReadRatesButton");
                    yearInput.Focus(); yearInput.Text = "2023";
                    readButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await shell.ExchangeRates.RefreshCommand.ExecutionTask!;
                    if (shell.ExchangeRates.Year != 2023 || shell.ExchangeRates.Rates.Count == 0
                        || shell.ExchangeRates.Rates.Any(rate => rate.Year != 2023))
                        throw new InvalidOperationException($"手动修改年度后点击读取未提交新年度：{shell.ExchangeRates.Year}，{yearInput.Text}，{shell.ExchangeRates.Rates.Count}，{shell.ExchangeRates.RateStatus}");
                    yearInput.SetCurrentValue(Wpf.Ui.Controls.NumberBox.ValueProperty, 2020d);
                    await shell.ExchangeRates.RefreshCommand.ExecuteAsync(null);
                    if (shell.ExchangeRates.Rates.Count != 0 || !shell.ExchangeRates.RateStatus.Contains("2020 年尚未配置汇率"))
                        throw new InvalidOperationException("未配置年度没有清空汇率或明确提示。");
                }
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (key == "tax") await Task.Delay(800);
                if (key == "history")
                {
                    // 最小窗口下分页栏不能被表格挤出可见区域。
                    await Task.Delay(400);
                    var historyPage = FindVisual<Views.Pages.HistoryPage>(window)!;
                    var bar = (FrameworkElement)historyPage.FindName("PagingBar");
                    var originalWidth = window.Width; var originalHeight = window.Height;
                    window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                    var bottom = bar.TranslatePoint(new Point(0,bar.ActualHeight),window).Y;
                    if (bottom > window.ActualHeight - 40 || bar.ActualWidth < 200)
                        throw new InvalidOperationException("最小窗口下历史分页栏不可见。");
                    window.Width = originalWidth; window.Height = originalHeight; window.UpdateLayout();
                }
                if (imagePath is not null && key is "dashboard" or "import" or "tax" or "history")
                {
                    // 等待导航过渡与字形渲染完成，截图反映最终页面。
                    await Task.Delay(400); window.UpdateLayout();
                    var file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(imagePath)!,
                        System.IO.Path.GetFileNameWithoutExtension(imagePath) + "-" + key + ".png");
                    Capture(window, file);
                }
            }
            if (bindingErrors.Errors.Count > 0) throw new InvalidOperationException("绑定错误：" + string.Join("\n", bindingErrors.Errors));
            var useCases = shell.State.UseCases;
            await useCases.SaveReviewAsync(result.SnapshotId, "合成复核", "合成来源，不含真实账户。");
            if ((await useCases.ReviewsAsync(result.SnapshotId)).Count != 1) throw new InvalidOperationException("复核保存失败。");
            var exportPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CRS-export-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try { await useCases.ExportAsync(exportPath, result, ""); if (new System.IO.FileInfo(exportPath).Length == 0) throw new InvalidOperationException("Excel 导出为空。"); }
            finally { System.IO.File.Delete(exportPath); }
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { await useCases.CalculateAsync(new([],2025,"IBKR",true,true,false,null,false),cts.Token); throw new InvalidOperationException("取消请求仍计算。"); }
            catch (OperationCanceledException) { }
            if ((await useCases.HistoryAsync()).Count != 1) throw new InvalidOperationException("取消请求仍保存。");
            shell.Review.Evidence = "上一份依据"; shell.Review.Confirmed = true; shell.Import.Year = 2024;
            if (shell.State.HasResult || shell.Reports.ExportCommand.CanExecute(null) || shell.Review.Confirmed || shell.Results.IncomeSeries.Length != 0)
                throw new InvalidOperationException("输入变化没有清除旧结果和复核依据。");
            var fixtureIndex = Array.IndexOf(args, "--fixture");
            if (fixtureIndex >= 0 && fixtureIndex + 1 < args.Length)
            {
                var outcome = await useCases.CalculateAsync(new([args[fixtureIndex+1]],2025,"IBKR",true,true,false,null,true),CancellationToken.None);
                if (outcome.Result.ImportedTradeCount != 2 || useCases.Trades(await useCases.LoadAsync(outcome.Result.SnapshotId)).Count != 2)
                    throw new InvalidOperationException("应用用例未保存可恢复交易快照。");
                // 用真实脱敏 XML 验证原件校验及复算持久化，原运行必须保持不变。
                var originalId = outcome.Result.SnapshotId;
                var requirements = await useCases.OriginalFileRequirementsAsync(originalId);
                if (requirements.Reports.Length != 1 || requirements.OpeningFile is not null)
                    throw new InvalidOperationException("历史原件要求与导入文件不一致。");
                await useCases.VerifyOriginalFilesAsync(originalId, [args[fixtureIndex + 1]], null, CancellationToken.None);
                try { await useCases.VerifyOriginalFilesAsync(originalId, [], null, CancellationToken.None);
                    throw new InvalidOperationException("缺少原件仍通过历史校验。"); }
                catch (CrsException) { }
                var replay = await useCases.ReplayAsync(originalId, CancellationToken.None);
                if (replay.ParentSnapshotId != originalId || replay.SnapshotId == originalId
                    || (await useCases.LoadAsync(originalId)).ParentSnapshotId is not null
                    || (await useCases.QueryHistoryAsync(1, 50)).TotalCount != 3)
                    throw new InvalidOperationException("复算未另存新运行或改写了原运行。");
                try { await useCases.ReplayAsync(originalId, cts.Token);
                    throw new InvalidOperationException("取消的复算仍执行。"); }
                catch (OperationCanceledException) { }
                if ((await useCases.QueryHistoryAsync(1, 50)).TotalCount != 3)
                    throw new InvalidOperationException("取消复算留下半成品历史。");
                // 实际窗口勾选两个券商的来源任务，验证汇总另存、导航和来源表格。
                var frozenRate = outcome.Result.Rates.Single(r => r.Currency == "USD");
                var annualIncome = new Income("ANNUAL-SECOND",2025,"USD",10,0);
                var annualSummary = TaxEngine.Calculate([annualIncome],[],new Dictionary<string,decimal>(),2025,(_,_)=>frozenRate.Value);
                var second = new CalculationResult { Year=2025,Broker="FUTU",SnapshotId="annual-source-futu",Summary=annualSummary,
                    CoveredAccounts=["ANNUAL-SECOND"],Rates=[frozenRate],TaxInputs=new(1,TaxEngine.PolicyVersion,[annualIncome],[],"FUTU"),
                    CalculationStatus=CalculationStatus.Completed,DataCompleteness=DataCompleteness.Confirmed,
                    ReconciliationStatus=ReconciliationStatus.Matched,UsageLabel=UsageLabel.ReviewReady,Reconciled=true,
                    EstimatedTopUpCny=annualSummary.SupplementTax };
                operations.Repository.Save(second);
                window.Navigate("history"); await shell.History.RefreshCommand.ExecuteAsync(null); await Task.Delay(400);
                shell.History.SelectableRows.Single(r=>r.Task.Id==originalId).IsIncluded=true;
                shell.History.SelectableRows.Single(r=>r.Task.Id==second.SnapshotId).IsIncluded=true;
                shell.History.OwnershipConfirmed=true;
                if (imagePath is not null) { window.UpdateLayout(); Capture(window,VariantPath(imagePath,"annual-selection")); }
                await shell.History.AggregateAnnualCommand.ExecuteAsync(null); await Task.Delay(500); window.UpdateLayout();
                if (shell.State.Result?.IsAnnualAggregate != true || shell.State.Result.AggregationSources.Count != 2
                    || shell.History.TotalCount != 5 || shell.History.AggregationSelection.Count != 0
                    || !shell.Results.TaxTables.Any(t=>t.Title=="年度汇总来源"))
                    throw new InvalidOperationException("窗口年度汇总未另存、清理选择或展示来源。");
                if (imagePath is not null) Capture(window,VariantPath(imagePath,"annual-tax"));
                if (bindingErrors.Errors.Count > 0) throw new InvalidOperationException("汇总绑定错误："+string.Join("\n",bindingErrors.Errors));
            }
            if(vault is not null && settings is not null)
            {
                // 所有业务检查完成后锁定隔离库，核验真实启动界面的绑定与绘制，不读取真实账户。
                vault.Lock(); var security=new VaultViewModel(vault,settings,new SmokeInteraction());
                var gate=new VaultWindow(security) {Owner=window}; gate.Show(); await Task.Delay(250); gate.UpdateLayout();
                if(security.ContinueCommand.CanExecute(null) || security.IsUnlocked) throw new InvalidOperationException("未解锁仍允许进入工作区。");
                if(security.ShowPasswordEntry || security.CreateCommand.CanExecute(null) || !security.VerifyCodeCommand.CanExecute(null)) throw new InvalidOperationException("本机日常手机登录仍显示主密码或创建入口。");
                if(security.ShowDirectoryEntry || ((FrameworkElement)gate.FindName("DirectoryPanel")).IsVisible) throw new InvalidOperationException("日常登录仍展示数据库位置。");
                if(imagePath is not null) Capture(gate,VariantPath(imagePath,"vault")); gate.Close();
                // 换机限制会话后再遇到系统锁屏，应能重复清理而不复用敏感模型。
                window.PrepareLock(); window.PrepareLock();
                if(window.DataContext is not null || shell.State.HasResult) throw new InvalidOperationException("锁定未清除敏感工作区。");
                if(bindingErrors.Errors.Count>0) throw new InvalidOperationException("保险库绑定出现错误。");
            }
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors); }
    }
    private static string VariantPath(string path, string suffix) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileNameWithoutExtension(path) + "-" + suffix + ".png");
    /// <summary>从已绘制的窗口查找控件，验证真实页面而非仅修改页面模型。</summary>
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path); encoder.Save(stream);
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
internal sealed class SmokeInteraction : IUserInteraction
{
    public string[] PickFiles(string filter, bool multiple) => [];
    public string? SaveFile(string filter, string fileName) => null;
    public void ShowError(string message) => throw new InvalidOperationException(message);
    public void OpenFolder(string path) => throw new InvalidOperationException("合成检查不打开外部目录。");
}
