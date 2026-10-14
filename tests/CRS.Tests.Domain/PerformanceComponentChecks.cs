using CRS.Domain;
using CRS.Domain.Performance;

internal static class PerformanceComponentChecks
{
    internal static void Run()
    {
        var from=new DateOnly(2025,1,1);var to=from.AddDays(1);var source=new PerformanceSource("SYNTHETIC","component");
        var account=new InvestmentAccount("A","SYNTHETIC","合成A","USD",from);
        ValuationSnapshot Nav(string id,DateOnly date,decimal amount)=>new(id,"A",new(date.ToDateTime(new(23,0)),TimeSpan.Zero),date,
            ValuationMoment.EndOfDay,[new("Cash","USD",amount)],source,PerformanceQuality.Verified,amount,"USD",true);
        var input=new PerformanceData{Accounts=[account],Valuations=[Nav("OPEN",from,100),Nav("END",to,110)]};
        var request=new PerformanceRequest(["A"],from,to,"USD");
        PerformanceInputValidator.Validate(input,request,default);
        try {PerformanceInputValidator.Validate(input,request with{Currency="?"},default);throw new Exception("结构校验接受了未知币种。");}catch(CrsException){}
        var context=new PerformanceCalculationContext(input,request,default);var diagnostics=new PerformanceDiagnostics(context);var valuation=new PerformanceValuationEngine(context,diagnostics);
        input.Valuations[0].Components[0]=new("Cash","USD",999);request.AccountIds[0]="mutated";
        if(valuation.Nav(from,ValuationMoment.EndOfDay)!=100 || context.Request.AccountIds[0]!="A")throw new Exception("计算上下文仍共享调用方可变数组。");
        var returns=new PerformanceReturnCalculator(context,diagnostics,valuation,[],[],from,100,110);
        if(returns.Twr().Rate!=.1m)throw new Exception("收益率组件无法独立计算。");
        var foreign=context.Data with{Fx=[new("F","USD","CNY",to,7,source,PerformanceQuality.Verified)]};
        var fxContext=new PerformanceCalculationContext(foreign,context.Request with{Currency="CNY"},default);var fxDiagnostics=new PerformanceDiagnostics(fxContext);
        var fxValuation=new PerformanceValuationEngine(fxContext,fxDiagnostics);
        if(fxValuation.Nav(to,ValuationMoment.EndOfDay)!=770 || fxValuation.Nav(from,ValuationMoment.EndOfDay) is not null || !fxDiagnostics.Issues.Any(i=>i.Code=="FX_MISSING"))throw new Exception("估值组件未独立保留历史汇率/缺失语义。");
        var transferDate=from.AddDays(1);var time=new DateTimeOffset(transferDate.ToDateTime(new(12,0)),TimeSpan.Zero);
        var outgoing=new InvestmentEvent("OUT","A",time,transferDate,InvestmentEventKind.CashTransfer,-50,"USD",source,Confirmed:true);
        var incoming=outgoing with{Id="IN",AccountId="B",Amount=50};
        var transferData=new PerformanceData{Accounts=[account,account with{Id="B",Alias="合成B"}],Events=[outgoing,incoming],Transfers=[new("L","OUT","IN",true)]};
        var transfers=new PerformanceCalculationContext(transferData,new(["A","B"],from,to,"USD"),default);var transferDiagnostics=new PerformanceDiagnostics(transfers);
        var flows=new PerformanceCashFlowClassifier(transfers,transferDiagnostics,new(transfers,transferDiagnostics)).Classify(transferData.Events);
        if(flows.Count!=2 || flows.Any(f=>!f.Internal) || flows.Sum(f=>f.Amount)!=0)throw new Exception("现金流组件将范围内转账当成外部投入。");
        diagnostics.Lower(PerformanceQuality.Estimated);diagnostics.Lower(PerformanceQuality.Verified);
        if(diagnostics.Quality!=PerformanceQuality.Estimated)throw new Exception("质量判定被后续证据静默提升。");
        Console.WriteLine("绩效组件通过：独立校验、上下文隔离、非美元估值、缺汇率、转账分类、收益率及质量门槛。");
    }
}
