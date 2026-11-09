using System.Windows;
using CRS.Timing;

namespace CRS.DesktopClient.Views;

public partial class ImportReviewWindow : Window
{
    public ImportDecision Decision { get; private set; } = ImportDecision.Cancel;

    public ImportReviewWindow(ImportReview review)
    {
        InitializeComponent();
        var exact = review.Kind == ImportMatchKind.Identical;
        Heading.Text = review.Kind switch
        {
            ImportMatchKind.Identical => "已有相同资料的核算结果",
            ImportMatchKind.SharedFiles => "部分资料已有导入记录",
            _ => "报表期间与已有记录重叠"
        };
        var history = review.Existing;
        var saved = BeijingTime.Display(history.CreatedUtc, "yyyy-MM-dd HH:mm");
        Existing.Text = $"已有记录　{history.Broker} · {history.Year} 年 · {saved}（北京时间）\n"
            + (history.Complete ? "结果完整" : $"结果待核对 · {history.Issues} 项待复核")
            + (history.IsArchived ? " · 已归档" : "");
        Files.Text = "本次相关报表　" + string.Join("、", review.Files.Take(4))
            + (review.Files.Count > 4 ? $" 等 {review.Files.Count} 份" : "");
        Detail.Text = review.Kind switch
        {
            ImportMatchKind.Identical => "建议打开已有结果。需要再保存一份核算记录时，可选择重新计算并另存。",
            ImportMatchKind.SharedFiles => "继续前请核对 " + string.Join("、", review.Differences) + "。",
            _ => "同一账户的同类报表覆盖了部分相同日期。请核对券商报表的范围和版本后再继续。"
        };
        if (exact && history.ActiveCopies > 1)
            Detail.Text += "\n这组结果有多条在用记录，可在「导入历史」中归档多余副本。";
        OpenExistingButton.Visibility = exact ? Visibility.Visible : Visibility.Collapsed;
        SaveNewButton.Content = exact ? "重新计算并另存" : "继续导入";
        if (!exact)
        {
            SaveNewButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            SaveNewButton.IsDefault = true;
        }
    }

    private void OpenExisting(object sender, RoutedEventArgs e) { Decision = ImportDecision.OpenExisting; Close(); }
    private void SaveNew(object sender, RoutedEventArgs e) { Decision = ImportDecision.SaveNew; Close(); }
}
