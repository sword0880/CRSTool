namespace CRS.Application;

/// <summary>统一界面与底稿的中文状态名称，序列化状态枚举保持稳定。</summary>
public static class ResultLabels
{
    /// <summary>说明计算执行的完成程度。</summary>
    public static string Calculation(CalculationStatus value) => value switch
    { CalculationStatus.Completed => "已完成", CalculationStatus.Partial => "部分完成", CalculationStatus.Blocked => "已阻断", _ => "未知" };
    /// <summary>说明资料完整性，不将未确认写成完整。</summary>
    public static string Completeness(DataCompleteness value) => value switch
    { DataCompleteness.Confirmed => "已确认", DataCompleteness.Unconfirmed => "未确认", DataCompleteness.Incomplete => "不完整", _ => "未知" };
    /// <summary>区分对账差异和缺少独立资料。</summary>
    public static string Reconciliation(ReconciliationStatus value) => value switch
    { ReconciliationStatus.Matched => "已匹配", ReconciliationStatus.Differences => "存在差异", ReconciliationStatus.NotVerifiable => "缺少独立核对资料", _ => "未知" };
    /// <summary>展示结果允许的使用范围。</summary>
    public static string Usage(UsageLabel value) => value switch
    { UsageLabel.ReviewReady => "可供复核", UsageLabel.Provisional => "临时测算", UsageLabel.ReviewOnly => "仅供人工复核", _ => "未知" };
}
