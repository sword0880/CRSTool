using CRS.Application;
using CRS.Domain;
using CRS.DesktopClient.ViewModels;

internal static class TaxAmountViewChecks
{
    /// <summary>页面顶部和表格不得把部分结果的预计字段误当可信金额，真实零值仍能展示。</summary>
    internal static async Task RunAsync()
    {
        var shell=new ShellViewModel(new FakeUseCases(),new SilentInteraction());
        await shell.State.DisplayAsync(new() {Year=2025,Summary=new(10,0,0,2,0,0,2),EstimatedTopUpCny=2,UnknownGainCount=1});
        var rows=shell.Results.TaxTables.Single(t=>t.Title=="税务汇总").Rows.Cast<TaxAmountLine>().ToArray();
        if(!shell.Results.Estimate.Contains("不可确定") || rows[^1].人民币金额 is not null || rows[^2].人民币金额!=2 || !rows[2].说明.Contains("1 笔"))
            throw new Exception("页面把部分或未知金额显示成完整年度预计值。");
        await shell.State.DisplayAsync(new() {Year=2025,Summary=new(0,0,0,0,0,0,0),EstimatedTopUpCny=0,Reconciled=true,
            CalculationStatus=CalculationStatus.Completed,DataCompleteness=DataCompleteness.Confirmed,
            ReconciliationStatus=ReconciliationStatus.Matched,UsageLabel=UsageLabel.ReviewReady});
        if(!shell.Results.Estimate.Contains("0.00") || shell.Results.Estimate.Contains("不可确定")) throw new Exception("页面丢失真实零值。");
        Console.WriteLine("金额界面通过：顶部和表格共用门槛，部分值不冒充预计补税、公式保留、未知收益说明与真实零值。");
    }
}
