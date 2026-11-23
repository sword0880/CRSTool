using System.Windows;
using CRS.Timing;

namespace CRS.DesktopClient.Views;

public partial class ImportHistoryComparisonWindow : Window
{
    public ImportHistoryComparisonWindow(IReadOnlyList<ImportHistoryComparison> rows)
    {
        InitializeComponent();
        ComparisonGrid.ItemsSource = rows.Select(row => new
        {
            row.Id,
            SavedAt = BeijingTime.Display(row.CreatedUtc,"yyyy-MM-dd HH:mm:ss"),
            Status = (row.Complete ? "完整" : $"待核对 {row.Issues} 项") + (row.IsArchived ? " · 已归档" : " · 在用"),
            row.RealizedGainCny,
            row.EstimatedTopUpCny,
            Differences = row.IsSelected ? "所选任务" : row.Differences.Count == 0 ? "已登记条件一致" : string.Join("、",row.Differences)
        }).ToArray();
    }
}
