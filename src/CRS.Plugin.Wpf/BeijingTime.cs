using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace CRS.Plugin.Wpf;

/// <summary>直接格式化内部已按北京时间记录的时间，不在界面平移时区。</summary>
public static class BeijingTime
{
    public static string Format(DateTimeOffset value, string format = "yyyy-MM-dd HH:mm:ss")
        => value.ToString(format, CultureInfo.InvariantCulture);
    public static object Display(object? value, string format = "yyyy-MM-dd HH:mm:ss")
    {
        if (value is null) return "";
        if (value is DateTimeOffset instant) return Format(instant, format);
        if (value is DateTime date && date.Kind != DateTimeKind.Unspecified) return Format(new DateTimeOffset(date), format);
        if (value is string text && Regex.IsMatch(text.Trim(), @"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return Format(parsed, format);
        return value; // 时区未记录的来源值不猜测为 UTC 或本机时区。
    }
}

/// <summary>同时支持 UTC 字符串、DateTimeOffset 和空结束时间。</summary>
public sealed class BeijingTimestampConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => BeijingTime.Display(value, parameter as string ?? "yyyy-MM-dd HH:mm:ss");
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length > 1 && values[1] is false && values[0] is DateTimeOffset original
            ? original.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "（原始时区未记录）"
            : Convert(values[0], targetType, parameter, culture);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
