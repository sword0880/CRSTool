using System.Globalization;
using System.Windows.Data;
namespace CRS.DesktopClient.Converters;
/// <summary>历史任务时间按北京时间显示；保留旧类型名以兼容已有资源声明。</summary>
public sealed class UtcTimestampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => CRS.Timing.BeijingTime.Display(value, "yyyy-MM-dd HH:mm");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
