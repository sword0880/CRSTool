using System.Globalization;
using System.Windows.Data;

namespace CRS.DesktopClient.Converters;

/// <summary>为指标和底部操作栏保留空间，账户设置内容在自身区域滚动。</summary>
public sealed class PerformanceScopeHeightConverter:IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
        =>value is double height && double.IsFinite(height)?Math.Clamp(height*.45,100,260):140d;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
