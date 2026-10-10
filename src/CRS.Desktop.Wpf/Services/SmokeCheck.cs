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
    public static async Task RunAsync(MainWindow window, ShellViewModel shell, IDesktopOperations operations, string[] args)
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
                if (imagePath is not null && key is "dashboard" or "import" or "tax")
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
