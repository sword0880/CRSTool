namespace CRS.Domain.Performance;

/// <summary>收益率调度：保留精确/日末重建 TWR 口径，资金加权收益复用 XirrSolver。</summary>
internal sealed class PerformanceReturnCalculator(PerformanceCalculationContext context,PerformanceDiagnostics diagnostics,
    PerformanceValuationEngine valuation,InvestmentEvent[] events,List<PerformanceFlow> flows,DateOnly effectiveFrom,decimal? opening,decimal? ending)
{
    private PerformanceData data=>context.Data;
    private PerformanceRequest request=>context.Request;
    private CancellationToken cancellation=>context.Cancellation;
    private IReadOnlyDictionary<string,InvestmentAccount> accounts=>context.Accounts;
    internal XirrResult Xirr()=>opening<=0 || ending<0?new(null,"零或负起始净值/负结束净值的杠杆区间不自动求解。"):XirrSolver.Solve(
    new[]{(effectiveFrom,-opening!.Value),(request.To,ending!.Value)}.Concat(flows.Where(f=>!f.Internal).Select(f=>(f.Date,-f.Amount))),cancellation);
    internal XirrResult Twr()
    {
        if(opening<=0 || ending<0)return new(null,"零/负净值或负分母区间不能自动链接收益。");
        if(!flows.Any(f=>!f.Internal))
        {
            // 没有外部流量时子期间链接会望远镜消去，不需要伪造每日曲线。
            if(accounts.Count==1 || request.Method==ReturnMethod.EndOfDayReconstructed)
                return new(ending!.Value/opening!.Value-1,"无外部流量：按两端净值计算区间累计；跨账户采用显式日末对齐时仅属重建口径。");
        }
        decimal factor=1,previous=opening!.Value;
        if(request.Method==ReturnMethod.Exact)
        {
            if(events.Any(e=>flows.Any(f=>!f.Internal && f.EventId==e.Id) && !e.TimeVerified))return new(null,"外部流量时区/时点未确认，不能使用日内假定计算精确 TWR。");
            var starts=accounts.Keys.Select(id=>context.Valuations[(id,effectiveFrom,ValuationMoment.EndOfDay)].First().AsOf).Distinct().Count();
            var ends=accounts.Keys.Select(id=>context.Valuations[(id,request.To,ValuationMoment.EndOfDay)].First().AsOf).Distinct().Count();
            if(starts!=1 || ends!=1 || data.Valuations.Any(v=>accounts.ContainsKey(v.AccountId) && (v.Date==effectiveFrom || v.Date==request.To) && v.Moment==ValuationMoment.EndOfDay && !v.TimeVerified))return new(null,"跨市场快照时点未确认或不是同一时点，不能声明精确 TWR；可明确选择日末假定重建。");
            foreach(var group in flows.Where(f=>!f.Internal).GroupBy(f=>f.Time).OrderBy(g=>g.Key))
            {
                var date=group.First().Date; var before=valuation.Nav(date,ValuationMoment.BeforeFlow,group.Key); var after=valuation.Nav(date,ValuationMoment.AfterFlow,group.Key);
                if(before is null || after is null || previous<=0 || before<0 || after<=0 || data.Valuations.Any(v=>accounts.ContainsKey(v.AccountId) && v.AsOf==group.Key && v.Moment!=ValuationMoment.EndOfDay && !v.TimeVerified))return new(null,"缺少已确认流量前后同一时点净值，或出现零/负分母。");
                if(Math.Abs(after.Value-before.Value-group.Sum(f=>f.Amount))>diagnostics.Tolerance(after.Value))return new(null,"流量前后净值与外部现金/实物流量不一致。");
                factor*=before.Value/previous;previous=after.Value;
            }
        }
        else
        {
            // 明确采用日末流量假定，不能将该重建结果命名为精确 TWR。
            for(var day=effectiveFrom.AddDays(1);day<=request.To;day=day.AddDays(1))
            {
                cancellation.ThrowIfCancellationRequested();var current=valuation.Nav(day,ValuationMoment.EndOfDay);
                if(current is null || previous<=0)return new(null,"日频重建需要区间内每个日历日完整日末净值；缺日不能自动沿用。");
                var gainBase=current.Value-flows.Where(f=>!f.Internal && f.Date==day).Sum(f=>f.Amount);
                if(gainBase<0)return new(null,"日末流量假定产生负链接基数，需人工核对。");
                factor*=gainBase/previous;previous=current.Value;
            }
            return new(factor-1,"日末外部流量假定下的日频几何链接；不是流量时点精确 TWR，含假定误差。");
        }
        return new(factor*ending!.Value/previous-1,"外部流量前后估值切分并几何链接；区间累计，未年化。");
    }
}
