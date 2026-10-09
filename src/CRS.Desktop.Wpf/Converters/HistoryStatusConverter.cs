using System.Globalization;
using System.Windows.Data;
namespace CRS.DesktopClient.Converters;
/// <summary>历史完整性转换为可读任务状态。</summary>
public sealed class HistoryStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "已完成" : "待核对";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
