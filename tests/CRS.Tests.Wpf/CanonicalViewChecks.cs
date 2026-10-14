using CRS.Application;
using CRS.Domain;
using CRS.DesktopClient.ViewModels;

internal static class CanonicalViewChecks
{
    /// <summary>损坏明细显示中文原因并清除旧交易，冻结汇总仍可查看。</summary>
    internal static async Task RunAsync()
    {
        var cases=new Cases(); var shell=new ShellViewModel(cases,new SilentInteraction());
        await shell.State.DisplayAsync(cases.Result);
        if(shell.Results.Trades.Count!=1) throw new Exception("有效交易没有展示。");
        cases.Damaged=true; await shell.State.DisplayAsync(cases.Result);
        if(shell.Results.Trades.Count!=0 || !shell.Results.TradesHint.Contains("快照损坏")
            || shell.Results.IncomeChart.Series.Length!=1 || shell.Results.TaxTables.Count==0 || !shell.Reports.ExportCommand.CanExecute(null))
            throw new Exception("损坏快照沿用旧交易、缺少原因或阻断了冻结汇总。");
        Console.WriteLine("快照展示通过：损坏明细清除旧交易、显示原因、保留冻结汇总和导出资格。");
    }
    private sealed class Cases:FakeUseCases
    {
        public bool Damaged {get;set;}
        public override IReadOnlyList<Trade> Trades(CalculationResult result)=>Damaged?throw new CrsException("快照损坏，请重新导入原件。"):
            [new(new("A","STOCK","USD"),"STOCK",new(2025,1,1,0,0,0,TimeSpan.Zero),true,"BUY",1,10,10,0,-10,"BUY","synthetic",1,"STK",null)];
    }
}
