using System.Globalization;

namespace CRS.Timing;

/// <summary>全应用系统时间口径。时间点在创建/读入边界即带 +08:00，展示层直接格式化。</summary>
public static class BeijingClock
{
    public const string ZoneId = "China Standard Time";
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    public static DateTimeOffset Now => Read(TimeProvider.System);
    public static DateTime Today => Now.Date;
    public static DateTimeOffset Read(TimeProvider provider) => Normalize(provider.GetUtcNow());
    public static DateTimeOffset Normalize(DateTimeOffset value) => value.ToOffset(Offset);
    public static DateTimeOffset At(DateOnly date, TimeOnly time) => new(date.ToDateTime(time), Offset);
    public static DateTimeOffset Parse(string value) => Normalize(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture));
    public static string Serialize(DateTimeOffset value) => Normalize(value).ToString("O", CultureInfo.InvariantCulture);
    public static string NormalizeText(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
        ? Serialize(instant) : value;
}
