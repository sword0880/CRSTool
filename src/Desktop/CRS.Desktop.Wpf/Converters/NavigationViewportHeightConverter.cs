using System.Globalization;
using System.Windows.Data;

namespace CRS.DesktopClient.Converters;

/// <summary>导航会重置 Page.Height，因此约束内部布局；扣除上下页边距后保留有限滚动区域。</summary>
public sealed class NavigationViewportHeightConverter:IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is double height && double.IsFinite(height)?Math.Max(0,height-40):0d;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
