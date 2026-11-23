using System.Globalization;
using System.Text.RegularExpressions;

namespace CRS.Timing;

/// <summary>直接格式化内部已按北京时间记录的时间；时区归一化由读入边界的BeijingClock负责。</summary>
public static class BeijingTime
{
    public static string Format(DateTimeOffset value,string format="yyyy-MM-dd HH:mm:ss")
        =>value.ToString(format,CultureInfo.InvariantCulture);
    public static object Display(object? value,string format="yyyy-MM-dd HH:mm:ss")
    {
        if(value is null)return "";
        if(value is DateTimeOffset instant)return Format(instant,format);
        if(value is DateTime date&&date.Kind!=DateTimeKind.Unspecified)return Format(new DateTimeOffset(date),format);
        if(value is string text&&Regex.IsMatch(text.Trim(),@"(?:Z|[+-]\d{2}:?\d{2})$",RegexOptions.IgnoreCase)
            &&DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))return Format(parsed,format);
        // 缺少时区的来源值保留原样，不猜测为UTC或本机时区。
        return value;
    }
}
