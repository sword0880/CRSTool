using System.Windows;
using System.Windows.Controls;
namespace CRS.DesktopClient.Views;
/// <summary>只负责中文表头和金额格式，不处理业务规则。</summary>
public static class GridPresentation
{
    public static readonly DependencyProperty UseChineseHeadersProperty = DependencyProperty.RegisterAttached("UseChineseHeaders", typeof(bool), typeof(GridPresentation), new PropertyMetadata(false, OnChanged));
    public static bool GetUseChineseHeaders(DependencyObject value) => (bool)value.GetValue(UseChineseHeadersProperty);
    public static void SetUseChineseHeaders(DependencyObject value, bool enabled) => value.SetValue(UseChineseHeadersProperty, enabled);
    /// <summary>为各表格挂接列展示处理。</summary>
    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        grid.AutoGeneratingColumn -= GenerateColumn;
        if ((bool)e.NewValue) grid.AutoGeneratingColumn += GenerateColumn;
    }
    /// <summary>格式化自动生成列，保持底层金额为 decimal。</summary>
    private static void GenerateColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        e.Column.Header = ColumnTitle(e.PropertyName);
        if (e.Column is DataGridTextColumn column && column.Binding is System.Windows.Data.Binding binding)
        {
            var type = Nullable.GetUnderlyingType(e.PropertyType) ?? e.PropertyType;
            if (e.PropertyName == "CreatedUtc") binding.Converter = new CRS.DesktopClient.Converters.UtcTimestampConverter();
            if (type == typeof(decimal)) { binding.StringFormat = e.PropertyName is "Value" or "Quantity" or "Price" ? "G29" : "N2"; if (Nullable.GetUnderlyingType(e.PropertyType) is not null) binding.TargetNullValue = "不可确定"; }
            if (type == typeof(DateOnly) || type == typeof(DateTime)) binding.StringFormat = "yyyy-MM-dd";
        }
    }
    /// <summary>将模型字段映射为中文表头，只负责展示名称。</summary>
    private static string ColumnTitle(string key) => key switch {
        "AccountName" => "账户名称", "Dividend" => "股息", "Interest" => "利息", "OtherIncome" => "其他收入（未计税）",
        "Sheet" => "工作表／页码", "Row" => "原始行号", "Type" => "类型", "Direction" => "方向", "Amount" => "金额", "Description" => "备注",
        "ExpectedGross" => "数量价格金额", "ReportedGross" => "原成交金额", "ExpectedNet" => "应变动金额", "ReportedNet" => "原变动金额",
        "Count" => "笔数", "Market" => "市场",
        "EstimatedTopUpCny" => "年度预计补税",
        "Account" => "账户", "Key" => "账户／证券／币种", "Symbol" => "证券", "Currency" => "币种", "Year" => "年度", "Id" or "TradeId" or "RecordId" => "记录编号",
        "Cost" => "成本", "Revenue" => "收入", "Quantity" => "数量", "BuyFee" => "买入费用", "SellFee" => "卖出费用", "Gain" => "收益", "GainCny" => "人民币收益",
        "BuyDate" => "买入日期", "SellDate" => "卖出日期", "BuyId" => "买入编号", "SellId" => "卖出编号", "BuyFile" => "买入文件", "SellFile" => "卖出文件",
        "Code" => "问题代码", "Message" => "说明", "Date" => "日期", "File" => "来源文件", "Instrument" => "证券标识", "AffectsCost" => "影响成本",
        "Start" => "起日", "End" => "止日", "Starting" => "期初现金", "Movement" => "明细变动", "CalculatedEnding" => "计算期末", "ReportedEnding" => "券商期末",
        "Difference" => "差额", "Status" => "状态", "Reason" => "原因", "Calculated" => "计算收益", "Reported" => "券商收益", "Value" => "汇率", "Source" => "来源",
        "Url" => "网址", "Method" => "统计方法", "Provisional" => "临时测算", "SourceConfirmed" => "来源已登记", "BuyTime" => "原始买入时间", "HasOffset" => "含时区",
        "SourceFile" => "来源文件", "Fee" => "剩余费用", "DividendCny" => "人民币股息", "InterestCny" => "人民币利息", "DividendInterestTax" => "股息利息税",
        "GainTax" => "收益税", "ForeignCredit" => "境外抵免", "SupplementTax" => "辅助测算补税", "CreatedUtc" => "保存时间UTC", "Complete" => "支持范围内完成",
        "Issues" => "待复核数量", "SnapshotId" => "计算编号", "Category" => "复核类型", "Evidence" => "复核依据", _ => key };
}
