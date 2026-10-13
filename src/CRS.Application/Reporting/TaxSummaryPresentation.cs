namespace CRS.Application;

/// <summary>前台和底稿共用的中文金额行，未知值保持 null，说明与金额一起展示。</summary>
public sealed record TaxAmountLine(string 项目,decimal? 人民币金额,string 说明);
public static class TaxSummaryPresentation
{
    /// <summary>只显示已保存且状态允许的预计值，绝不从原始公式或旧 Summary 字段补算。</summary>
    public static decimal? EstimatedAmount(CalculationResult result)=>result.Complete
        && result.DataCompleteness==DataCompleteness.Confirmed && result.ReconciliationStatus==ReconciliationStatus.Matched
        && result.UsageLabel==UsageLabel.ReviewReady && result.Rates.All(r=>r.SourceConfirmed) ? result.EstimatedTopUpCny : null;
    public static IReadOnlyList<TaxAmountLine> Rows(CalculationResult result)
    {
        var summary=result.Summary; var estimated=EstimatedAmount(result);
        return new TaxAmountLine[] {
            new("股息",summary.DividendCny,"按已纳入的年度收入和冻结汇率计算；完整性以任务状态为准。"),
            new("利息",summary.InterestCny,"按已纳入的年度收入和冻结汇率计算；完整性以任务状态为准。"),
            new("证券收益（已确定部分）",summary.GainCny,result.UnknownGainCount>0
                ? $"有 {result.UnknownGainCount} 笔收益因成本等资料无法确定，未纳入该金额；不能当作完整年度收益。"
                : "已确定的收益金额；完整性和对账状态以任务说明为准。"),
            new("股息利息辅助税额",summary.DividendInterestTax,"现有辅助公式金额，所得来源与实际申报口径仍需核对。"),
            new("证券收益辅助税额",summary.GainTax,"仅根据已确定收益计算；未知收益不以零值代入。"),
            new("辅助境外抵免",summary.ForeignCredit,"用于公式核对；凭证、国家分配、协定等法定条件仍需独立核实。"),
            new("辅助测算补税（原始公式）",summary.SupplementTax,"仅供核对原始公式，不代表允许使用的完整年度预计补税或申报金额。"),
            new("年度预计补税（状态允许时）",estimated,estimated is null
                ? "不可确定：资料、对账、临时状态或预计值未满足条件；空白不代表零，也不从原始公式补填。"
                : "按冻结结果状态允许显示的辅助预计值；仍需按实际适用规则复核，不是申报结论。")
        };
    }
}
