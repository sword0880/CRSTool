using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace CRS.DesktopClient.Views;

public partial class HistoricalRecalculationWindow : Window
{
    private readonly HistoricalRecalculationPlan plan;
    private readonly OriginalChoice[] choices;
    private string? carryPath;
    public HistoricalRecalculationRequest? Request { get; private set; }

    public HistoricalRecalculationWindow(HistoricalRecalculationPlan plan)
    {
        InitializeComponent();
        this.plan = plan;
        Heading.Text = $"{plan.Broker} · {plan.Year} 年";
        Basis.Text = "使用当前规则和汇率重新读取原件，另存一条与旧任务关联的结果。"
            + $"\n沿用原任务的范围确认（{(plan.ScopeConfirmed ? "已确认" : "待核对")}）"
            + $"和期初零持仓声明（{(plan.OpeningZero ? "已确认" : "未声明")}）。"
            + (plan.HasAccountPeriods ? "沿用已登记的账户期间依据。" : "")
            + (plan.HasVaultCarry ? "期初结转已从保险库核对。" : "");
        choices = plan.Originals.Select(original => new OriginalChoice(original)).ToArray();
        Originals.ItemsSource = choices;
        CarryPanel.Visibility = plan.NeedsCarry ? Visibility.Visible : Visibility.Collapsed;
        RefreshAvailability();
    }

    private void PickOriginal(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OriginalChoice choice }) return;
        var filter = Path.GetExtension(choice.Original.Name).ToLowerInvariant() switch
        {
            ".xml" => "IBKR 原件|*.xml",
            ".xlsx" => "富途 Excel 原件|*.xlsx",
            ".pdf" => "富途 PDF 原件|*.pdf",
            _ => "原始报表|*.*"
        };
        if (PickFile(filter) is not { } path) return;
        choice.LocalPath = path;
        RefreshAvailability();
    }

    private void PickCarry(object sender, RoutedEventArgs e)
    {
        if (PickFile("C# LOT 结转|*.json") is not { } path) return;
        carryPath = path;
        CarryLabel.Text = Path.GetFileName(path);
        RefreshAvailability();
    }

    private string? PickFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void RefreshAvailability() => CalculateButton.IsEnabled = choices.All(c => c.Original.InVault || c.LocalPath is not null)
        && (!plan.NeedsCarry || carryPath is not null);

    private void Calculate(object sender, RoutedEventArgs e)
    {
        Request = new HistoricalRecalculationRequest(plan.SourceId,choices.Where(c => c.LocalPath is not null)
            .ToDictionary(c => c.Original.Name,c => c.LocalPath!,StringComparer.OrdinalIgnoreCase),carryPath,SaveSnapshot.IsChecked == true);
        DialogResult = true;
    }

    private sealed class OriginalChoice(HistoricalOriginal original) : ObservableObject
    {
        private string? localPath;
        public HistoricalOriginal Original => original;
        public string Label => $"{KindLabel} · {original.Name}";
        private string KindLabel => original.Kind switch
        {
            HistoricalOriginalKind.Opening => "期初 LOT",
            HistoricalOriginalKind.Income => "收入报表",
            _ => "年度报表"
        };
        public string Status => LocalPath is { } path ? $"已选择 {Path.GetFileName(path)}，待校验"
            : original.InVault ? "保险库已有，核算时校验" : "缺少原件，请选择文件";
        public string? LocalPath
        {
            get => localPath;
            set { if (SetProperty(ref localPath,value)) OnPropertyChanged(nameof(Status)); }
        }
    }
}
