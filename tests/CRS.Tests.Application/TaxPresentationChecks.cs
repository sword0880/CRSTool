using CRS.Application;
using CRS.Domain;

internal static class TaxPresentationChecks
{
    /// <summary>部分结果、真实零值和旧 Summary 字段不可混淆，展示不改变冻结业务结果。</summary>
    internal static void Run()
    {
        var partial=new CalculationResult {Year=2025,Summary=new(10,0,0,2,0,0,2,99),UnknownGainCount=1,EstimatedTopUpCny=2};
        var rows=TaxSummaryPresentation.Rows(partial);
        if(rows[^1].人民币金额 is not null || !rows[^1].说明.Contains("不可确定") || rows[^2].人民币金额!=2
            || !rows[2].说明.Contains("1 笔") || !rows[^2].说明.Contains("原始公式") || partial.Summary.EstimatedTopUpCny!=99)
            throw new Exception("部分结果或旧字段被展示成完整预计值。");
        var complete=new CalculationResult {Year=2025,Summary=new(0,0,0,0,0,0,0),CalculationStatus=CalculationStatus.Completed,
            DataCompleteness=DataCompleteness.Confirmed,ReconciliationStatus=ReconciliationStatus.Matched,UsageLabel=UsageLabel.ReviewReady,Reconciled=true,EstimatedTopUpCny=0};
        if(TaxSummaryPresentation.Rows(complete)[^1].人民币金额!=0) throw new Exception("真实已确定零值被当作未知。");
        var legacy=new CalculationResult {Year=2025,Summary=new(0,0,0,0,0,0,0,10)};
        if(TaxSummaryPresentation.Rows(legacy)[^1].人民币金额 is not null) throw new Exception("旧 Summary 字段被推定成年度预计值。");
        Console.WriteLine("金额投影通过：部分／未知不补零、原始公式分列、未知收益说明、真实零值及旧预计字段不回填。");
    }
}
