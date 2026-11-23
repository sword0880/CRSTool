namespace CRS.Maintenance;

/// <summary>后台诊断文件的保留天数及设置修订，供服务和前台共用。</summary>
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record WorkerLogSettings(int RetentionDays, long Revision)
{
    public const int DefaultRetentionDays = 14;
    public const int MaximumRetentionDays = 365;

    public WorkerLogSettings() : this(DefaultRetentionDays, 0) { }

    public static bool IsValidRetention(int days) => days is >= 1 and <= MaximumRetentionDays;
}
