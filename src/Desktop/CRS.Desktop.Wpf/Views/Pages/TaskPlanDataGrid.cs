using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace CRS.DesktopClient.Views.Pages;

public sealed class TaskPlanDataGrid : DataGrid
{
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("DG_ScrollViewer") is not ScrollViewer scrollViewer) return;

        scrollViewer.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(scrollViewer) == 0 || VisualTreeHelper.GetChild(scrollViewer, 0) is not Grid chrome) return;

        var corner = chrome.Children.OfType<Button>().FirstOrDefault(button => button.Command == SelectAllCommand
            && BindingOperations.GetBinding(button, WidthProperty)?.Path?.Path == nameof(CellsPanelHorizontalOffset));
        // 横向滚动可产生负偏移，模板的全选按钮只接受非负宽度。
        corner?.SetBinding(WidthProperty, new Binding(nameof(CellsPanelHorizontalOffset))
        {
            Source = this,
            Mode = BindingMode.OneWay,
            Converter = NonNegativeWidth.Instance
        });
    }

    private sealed class NonNegativeWidth : IValueConverter
    {
        internal static readonly NonNegativeWidth Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double width && double.IsFinite(width) ? Math.Max(0, width) : 0d;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }
}
