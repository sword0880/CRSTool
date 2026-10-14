using CRS.Domain;
using CRS.Domain.Performance;

internal static class PerformanceChecks
{
    private static readonly DateOnly From=new(2024,12,31),To=new(2025,12,31),FlowDay=new(2025,6,30);
    private static PerformanceSource Source(string row)=>new("SYNTHETIC",row);
    private static DateTimeOffset At(DateOnly date,int hour=23)=>new(date.ToDateTime(new(hour,0)),TimeSpan.Zero);
    private static ValuationSnapshot Nav(string id,DateOnly date,decimal amount,ValuationMoment moment=ValuationMoment.EndOfDay,int hour=23)
        =>new(id+date+moment,id,At(date,hour),date,moment,[new("Cash","USD",amount)],Source(id+date+moment),PerformanceQuality.Verified,amount,"USD",true,true);
    private static PerformanceData Base(decimal start=10000,decimal end=11000)=>new()
    {Accounts=[new("A","IBKR","合成账户 A","USD",From)],Valuations=[Nav("A",From,start),Nav("A",To,end)],Coverage=[new("A",From,To,true,Source("coverage"),IncomeComplete:true,CostsComplete:true)]};
    private static PerformanceRequest Request(params string[] ids)=>new(ids.Length==0?["A"]:ids,From,To,"USD");
    private static decimal? Metric(PerformanceResult r,string id)=>r.Metrics.Single(m=>m.Id==id).Value;
    private static InvestmentEvent Event(string id,string account,InvestmentEventKind kind,decimal amount,DateOnly? date=null,string security="",decimal quantity=0,decimal ratio=0)
        =>new(id,account,At(date??FlowDay,12),date??FlowDay,kind,amount,"USD",Source(id),security,security==""?"":"US",quantity,Confirmed:true,SplitRatio:ratio,Quality:PerformanceQuality.Verified,TimeVerified:true);
    public static void Run()
    {
        var absent=PerformanceCalculator.Calculate(Base() with{Valuations=[],Coverage=[]},Request());
        if(Metric(absent,"Twr") is not null || Metric(absent,"Xirr") is not null || Metric(absent,"IncomeNet") is not null)throw new Exception("PA-01 缺资料被当作零/精确回报。");
        var simple=PerformanceCalculator.Calculate(Base(),Request());Equal(Metric(simple,"Twr"),.1m,"PA-02 TWR");Near(Metric(simple,"Xirr"),.1m,.000000001m,"PA-02 XIRR");
        var added=Base(10000,15000) with{Events=[Event("D","A",InvestmentEventKind.Deposit,5000)],Valuations=[Nav("A",From,10000),Nav("A",FlowDay,10000,ValuationMoment.BeforeFlow,12),Nav("A",FlowDay,15000,ValuationMoment.AfterFlow,12),Nav("A",To,15000)]};
        var unchanged=PerformanceCalculator.Calculate(added,Request());Equal(Metric(unchanged,"Profit"),0,"PA-03 损益");Equal(Metric(unchanged,"Twr"),0,"PA-03 TWR");Near(Metric(unchanged,"Xirr"),0,.000000001m,"PA-03 XIRR");
        var missingPoint=PerformanceCalculator.Calculate(added with{Valuations=added.Valuations.Where(v=>v.Moment==ValuationMoment.EndOfDay).ToArray()},Request());
        if(Metric(missingPoint,"Twr") is not null || Metric(missingPoint,"Profit")!=0)throw new Exception("缺流量估值时没有按指标独立阻断。");
        var transfer=Base(100,50) with{Accounts=[new("A","FUTU","富途合成","USD",From),new("B","IBKR","IBKR 合成","USD",From)],Valuations=[Nav("A",From,100),Nav("A",To,50),Nav("B",From,0),Nav("B",To,50)],Coverage=[new("A",From,To,true,Source("A")),new("B",From,To,true,Source("B"))],Events=[Event("OUT","A",InvestmentEventKind.CashTransfer,-50),Event("IN","B",InvestmentEventKind.CashTransfer,50)],Transfers=[new("L","OUT","IN",true)]};
        var together=PerformanceCalculator.Calculate(transfer,Request("A","B"));Equal(Metric(together,"NetInvestment"),0,"PA-04 内部流量");Equal(Metric(together,"Profit"),0,"PA-04 损益");
        var separate=PerformanceCalculator.Calculate(transfer,Request("A"));Equal(Metric(separate,"NetInvestment"),-50,"PA-16 单边外部流量");Equal(Metric(separate,"Profit"),0,"PA-16 单边损益");
        var fx=PerformanceCalculator.Calculate(Base(100,100) with{Events=[Event("FX","A",InvestmentEventKind.FxExchange,100)]},Request());Equal(Metric(fx,"NetInvestment"),0,"PA-05 换汇");Equal(Metric(fx,"Profit"),0,"PA-05 损益");
        var dividend=PerformanceCalculator.Calculate(Base(100,190) with{Events=[Event("DIV","A",InvestmentEventKind.Dividend,100) with{Withholding=10}]},Request());Equal(Metric(dividend,"IncomeNet"),90,"PA-07 股息净额");Equal(Metric(dividend,"NetInvestment"),0,"PA-07 不是入金");
        var margin=Base(100,100) with{Valuations=[Nav("A",From,100),Nav("A",To,100) with{Components=[new("Security","USD",120,"DEMO","US",1),new("Liability","USD",20)]}],Events=[Event("LOAN","A",InvestmentEventKind.Borrowing,20)]};
        var borrowed=PerformanceCalculator.Calculate(margin,Request());Equal(Metric(borrowed,"EndingNav"),100,"PA-08 净资产");Equal(Metric(borrowed,"NetInvestment"),0,"PA-08 借款");
        var missingPrice=PerformanceCalculator.Calculate(Base() with{Valuations=[Nav("A",From,10000),Nav("A",To,11000) with{Components=[new("Security","USD",null,"DEMO","US",1)]}]},Request());
        if(Metric(missingPrice,"EndingNav") is not null || !missingPrice.Issues.Any(i=>i.Code=="PRICE_MISSING"))throw new Exception("PA-10 缺行情被猜测。");
        var converted=Base() with{Fx=[new("R0","USD","CNY",From,7,Source("FX0"),PerformanceQuality.Verified),new("R1","USD","CNY",To,8,Source("FX1"),PerformanceQuality.Verified)]};
        var cny=PerformanceCalculator.Calculate(converted,Request() with{Currency="CNY"});Equal(Metric(cny,"Profit"),18000,"PA-11 历史汇率");
        var multi=XirrSolver.Solve([(new(2020,1,1),-100m),(new(2020,12,31),230m),(new(2021,12,31),-132m)]);
        if(multi.Rate is not null || !multi.Explanation.Contains("多个"))throw new Exception("PA-12 多解选择了任意一根。");
        if(XirrSolver.Solve([(From,100m),(To,110m)]).Rate is not null)throw new Exception("PA-12 缺符号返回数值。");
        var half=Base() with{Accounts=[new("A","IBKR","半年度合成","USD",FlowDay)],Valuations=[Nav("A",FlowDay,10000),Nav("A",To,11000)],Coverage=[new("A",FlowDay,To,true,Source("half"))]};
        var inception=PerformanceCalculator.Calculate(half,Request() with{From=new(2025,1,1),SinceInception=true});if(inception.EffectiveFrom!=FlowDay || !inception.Issues.Any(i=>i.Code=="TRUSTED_START"))throw new Exception("PA-13 虚构成立以来。");
        var again=PerformanceCalculator.Calculate(Base(),Request());if(System.Text.Json.JsonSerializer.Serialize(simple)!=System.Text.Json.JsonSerializer.Serialize(again))throw new Exception("PA-15 不确定输出。");
        var split=Base(100,100) with{Valuations=[Nav("A",From,100) with{Components=[new("Security","USD",100,"DEMO","US",1)]},Nav("A",To,100) with{Components=[new("Security","USD",100,"DEMO","US",2)]}],Events=[Event("SPLIT","A",InvestmentEventKind.Split,0,security:"DEMO",ratio:2)]};
        var splitResult=PerformanceCalculator.Calculate(split,Request());Equal(Metric(splitResult,"Profit"),0,"PA-18 拆股损益");Equal(Metric(splitResult,"Unrealized"),0,"PA-18 区间成本");
        var shares=transfer with{Valuations=[Nav("A",From,100) with{Components=[new("Security","USD",100,"DEMO","US",2)]},Nav("A",To,50) with{Components=[new("Security","USD",50,"DEMO","US",1)]},Nav("B",From,0),Nav("B",To,50) with{Components=[new("Security","USD",50,"DEMO","US",1)]}],Events=[Event("OUT","A",InvestmentEventKind.SecurityTransfer,-50,security:"DEMO",quantity:1),Event("IN","B",InvestmentEventKind.SecurityTransfer,50,security:"DEMO",quantity:1)]};
        var shared=PerformanceCalculator.Calculate(shares,Request("A","B"));Equal(Metric(shared,"Profit"),0,"PA-06 证券转仓");Equal(Metric(shared,"Unrealized"),0,"PA-06 转仓成本连续");
        // 官方示例公布值已舍入，采用比其迭代精度更严格的 5e-9 容差交叉核对。
        var excel=XirrSolver.Solve([(new(2008,1,1),-10000m),(new(2008,3,1),2750m),(new(2008,10,30),4250m),(new(2009,2,15),3250m),(new(2009,4,1),2750m)]);Near(excel.Rate,.373362535m,.000000005m,"Microsoft XIRR 官方示例");
        var daily=Base(100,110) with{Valuations=[Nav("A",From,100),Nav("A",From.AddDays(1),105),Nav("A",From.AddDays(2),110)],Coverage=[new("A",From,From.AddDays(2),true,Source("daily"))]};
        Equal(Metric(PerformanceCalculator.Calculate(daily,Request() with{To=From.AddDays(2),Method=ReturnMethod.EndOfDayReconstructed}),"Twr"),.1m,"日频几何链接");
        // 错误质量、复权价格和币种切换后的容差不能被当作可用证据。
        var badFx=PerformanceCalculator.Calculate(converted with{Fx=converted.Fx.Select(f=>f with{Quality=PerformanceQuality.InsufficientData}).ToArray()},Request() with{Currency="CNY"});if(Metric(badFx,"EndingNav") is not null)throw new Exception("资料不足汇率仍被使用。");
        var unknownNav=PerformanceCalculator.Calculate(Base() with{Valuations=[Nav("A",From,10000),Nav("A",To,11000) with{Quality=PerformanceQuality.InsufficientData}]},Request());if(Metric(unknownNav,"EndingNav") is not null)throw new Exception("不完整快照仍被估值。");
        var tolerance=transfer with{Events=[Event("OUT","A",InvestmentEventKind.CashTransfer,-50),Event("IN","B",InvestmentEventKind.CashTransfer,50.5m)],Transfers=[new("L","OUT","IN",true,1,"合成差额","CNY")],Fx=[new("TOL","CNY","USD",FlowDay,1m/7,Source("容差汇率"))]};
        if(!PerformanceCalculator.Calculate(tolerance,Request("A","B")).Issues.Any(i=>i.Code=="TRANSFER_DIFFERENCE"))throw new Exception("冻结人民币容差被解释成美元。");
        var bridge=PerformanceCalculator.Calculate(Base() with{Coverage=[new("A",From,To,true,Source("独立合成桥接"),ExplainedProfit:700,Currency:"USD",IncomeComplete:true,CostsComplete:true)]},Request());
        if(Metric(bridge,"Profit") is not null || Metric(bridge,"Twr") is not null || Metric(bridge,"ReconciliationDifference")!=300 || bridge.Accounts.Single().Profit is not null)throw new Exception("独立损益证据冲突仍显示已通过的收益率。");
        Console.WriteLine("绩效领域通过：缺资料、NAV/TWR/XIRR、流量时点评估、现金/证券转账边界、换汇、股息预扣、融资、缺价格、历史汇率、多根、半年度可信起点、确定性、拆股及 Microsoft 官方 XIRR 示例。");
    }
    private static void Equal(decimal? actual,decimal expected,string name) {if(actual!=expected)throw new Exception($"{name}：{actual} != {expected}");}
    private static void Near(decimal? actual,decimal expected,decimal tolerance,string name) {if(actual is null || Math.Abs(actual.Value-expected)>tolerance)throw new Exception($"{name}：{actual} != {expected}");}
}
