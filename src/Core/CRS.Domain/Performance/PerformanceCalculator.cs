namespace CRS.Domain.Performance;

/// <summary>只消费冻结绩效资料；编排估值、流量、收益率、成本与证据，不读取税务 FIFO。</summary>
public static class PerformanceCalculator
{
    public const string Version="Performance.v1";
    public static PerformanceResult Calculate(PerformanceData data,PerformanceRequest request,CancellationToken cancellation=default)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            PerformanceInputValidator.Validate(data,request,cancellation);
            return new Evaluation(new(data,request,cancellation)).Run();
        }
        catch(OverflowException) {throw new CrsException("绩效金额、汇率或复合收益超出可计算范围，请核对来源。");}
    }

    private sealed class Evaluation
    {
        private readonly PerformanceCalculationContext context;
        private readonly PerformanceDiagnostics diagnostics;
        private readonly PerformanceValuationEngine valuation;
        private PerformanceData data=>context.Data;
        private PerformanceRequest request=>context.Request;
        private CancellationToken cancellation=>context.Cancellation;
        private IReadOnlyDictionary<string,InvestmentAccount> accounts=>context.Accounts;
        private decimal? opening,ending;
        private DateOnly effectiveFrom;
        private InvestmentEvent[] events=[];
        private List<PerformanceFlow> flows=[];
        internal Evaluation(PerformanceCalculationContext context)
        {this.context=context;diagnostics=new(context);valuation=new(context,diagnostics);}
        internal PerformanceResult Run()
        {
            PrepareInputs();
            var metrics=BuildMetrics();
            var accountRows=BuildAccountRows();
            return new PerformanceResult(Version,request,metrics.ToArray(),diagnostics.Issues.Distinct().ToArray(),flows.ToArray(),accountRows,diagnostics.Reconciliations.Distinct().ToArray(),effectiveFrom,"IntervalMarketFIFO.v1"){Exposure=diagnostics.Exposure.ToArray()};
        }
        private void PrepareInputs()
        {
            effectiveFrom=request.From;
            if(request.SinceInception)
            {
                var starts=accounts.Values.Select(a=>context.AccountValuations[a.Id].Where(v=>v.Moment==ValuationMoment.EndOfDay).Select(v=>(DateOnly?)v.Date).Min()).ToArray();
                if(starts.All(x=>x is not null)) effectiveFrom=starts.Max()!.Value;
                if(effectiveFrom!=request.From) diagnostics.Issue("TRUSTED_START","","成立以来只能从共同可用净值起点计算，未回填开户日或零净值。","提供更早完整净值和资金流，或选择共同区间。",blocks:false);
                if(effectiveFrom>request.To) throw new CrsException("共同可信起点晚于结束日期。");
            }
            opening=valuation.Nav(effectiveFrom,ValuationMoment.EndOfDay); ending=valuation.Nav(request.To,ValuationMoment.EndOfDay);
            events=data.Events.Select(e=>context.EffectiveEvents[e.Id]).Where(e=>accounts.ContainsKey(e.AccountId) && e.Date>=effectiveFrom && e.Date<=request.To)
                .Where(e=>valuation.InWindow(e,effectiveFrom)).OrderBy(e=>e.Time).ThenBy(e=>e.Id,StringComparer.Ordinal).ToArray();
            foreach(var e in events){PerformanceClassifications.ValidateDirection(e);diagnostics.Lower(e.Quality);}
            foreach(var a in accounts.Values)
            {
                if(!a.BaseCurrencyConfirmed)diagnostics.Issue("ACCOUNT_CURRENCY_UNCONFIRMED",a.Id,"账户基础币种尚未核对，原币组成仍按各自显式币种估值。","在资料补录中核对并确认账户基础币种。",blocks:false);
                if(effectiveFrom<a.TrustedFrom) diagnostics.Issue("INITIAL_NAV_MISSING",a.Id,"所选起点早于账户登记的可信起点。","补齐期初资料或缩小区间。");
                if(!Covered(a.Id)) diagnostics.Issue("CASH_FLOW_COVERAGE",a.Id,"外部资金流完整性未确认覆盖整个区间，不能按零流量处理。","导入完整结单并补录/核对期间台账。");
            }
            flows=new PerformanceCashFlowClassifier(context,diagnostics,valuation).Classify(events);
        }
        private List<PerformanceMetric> BuildMetrics()
        {
            var blocked=valuation.Failed || opening is null || ending is null || diagnostics.Issues.Any(i=>i.BlocksReturns);
            var net=blocked?(decimal?)null:flows.Where(f=>!f.Internal).Sum(f=>f.Amount);
            var profit=net is null?null:ending-opening-net;
            var metrics=new List<PerformanceMetric>();
            Add("OpeningNav","期初净资产",opening,"金额",diagnostics.Quality,"证券+现金+已确认应收-融资负债-已确认应付；保留原币和来源。");
            Add("EndingNav","期末净资产",ending,"金额",diagnostics.Quality,"按报告日历史汇率折算；不同市场结单时点按日末口径聚合。");
            Add("NetInvestment","区间外部净投入",net,"金额",blocked?PerformanceQuality.InsufficientData:diagnostics.Quality,"仅当前范围的外部资金和实物资产流；股息、借款、换汇不是投入。");
            Add("Profit","区间投资损益",profit,"金额",blocked?PerformanceQuality.InsufficientData:diagnostics.Quality,"期末净资产-期初净资产-外部净投入；账户净回报，不扣未真实扣款的个人所得税。");
            decimal? cumulative=null;
            var commonStart=accounts.Values.Select(a=>context.AccountValuations[a.Id].Where(v=>v.Moment==ValuationMoment.EndOfDay).Select(v=>(DateOnly?)v.Date).Min()).ToArray();
            if(commonStart.All(x=>x.HasValue) && commonStart.Max()==effectiveFrom) cumulative=profit;
            Add("CumulativeProfit","可信起点累计投资损益",cumulative,"金额",cumulative is null?PerformanceQuality.InsufficientData:diagnostics.Quality,cumulative is null?"当前区间未从共同可信净值起点开始；选择成立以来重算。":"仅从共同可信起点累计，不代表开户以来。");
            var returns=new PerformanceReturnCalculator(context,diagnostics,valuation,events,flows,effectiveFrom,opening,ending);
            var twr=blocked?new XirrResult(null,"净值、资金流或异常资料不足。"):returns.Twr();
            Add("Twr",request.Method==ReturnMethod.Exact?"TWR（区间累计）":"TWR（日末假定重建，区间累计）",twr.Rate,"比例",twr.Rate is null?PerformanceQuality.InsufficientData:request.Method==ReturnMethod.Exact?diagnostics.Quality:PerformanceDiagnostics.Max(diagnostics.Quality,PerformanceQuality.Reconstructed),twr.Explanation);
            var xirr=blocked?new XirrResult(null,"期初/期末净值或外部流量资料不足。"):returns.Xirr();
            Add("Xirr","XIRR（资金加权年化）",xirr.Rate,"比例",xirr.Rate is null?PerformanceQuality.InsufficientData:diagnostics.Quality,xirr.Explanation,true);
            var incomeComplete=accounts.Keys.All(id=>Covered(id,c=>c.IncomeComplete));var costsComplete=accounts.Keys.All(id=>Covered(id,c=>c.CostsComplete));
            decimal? Income(InvestmentEventKind kind)=>incomeComplete?valuation.SumEvents(events.Where(e=>e.Kind==kind),e=>e.Amount-e.Withholding):null;
            var dividend=Income(InvestmentEventKind.Dividend); var interest=Income(InvestmentEventKind.Interest);
            var withholding=valuation.SumEvents(events.Where(e=>e.Kind==InvestmentEventKind.WithholdingTax),e=>Math.Abs(e.Amount));
            Add("DividendGross","股息毛额",incomeComplete?valuation.SumEvents(events.Where(e=>e.Kind==InvestmentEventKind.Dividend),e=>e.Amount):null,"金额",diagnostics.Quality,"须有完整收入台账；源泉税在净收入另扣，不列外部入金。");
            Add("IncomeNet","股息与利息净收入",dividend+interest-withholding,"金额",diagnostics.Quality,"股息、利息减附带预扣税及独立预扣税事件；不包含个人最终补税。");
            Add("Fees","交易与其他费用",costsComplete?valuation.SumEvents(events.Where(e=>e.Kind is InvestmentEventKind.Fee or InvestmentEventKind.Buy or InvestmentEventKind.Sell or InvestmentEventKind.FxExchange),e=>e.Kind==InvestmentEventKind.Fee?Math.Abs(e.Amount):e.Fees):null,"金额",diagnostics.Quality,"须有完整费用台账；成交附带费用与独立费用只保留一份来源分录。");
            Add("FinancingCost","融资利息",costsComplete?valuation.SumEvents(events.Where(e=>e.Kind==InvestmentEventKind.FinancingInterest),e=>Math.Abs(e.Amount)):null,"金额",diagnostics.Quality,"须有完整费用台账；借入本金不是外部投入，融资利息是投资成本。");
            // 资金流/复杂事件已经阻断时，区间成本结果也不能宣称完整。
            var cost=blocked?new PerformanceCostResult(null,null,"净值、资金流或复杂事件未核对，区间成本损益暂不可计算。"):
                PerformanceCostCalculator.Calculate(data,events,accounts.Keys.ToArray(),effectiveFrom,request.To,
                (amount,currency,date)=>valuation.Convert(amount,currency,date),cancellation);
            Add("Realized","区间已实现市场损益",cost.Realized,"金额",cost.Realized is null?PerformanceQuality.InsufficientData:PerformanceDiagnostics.Max(diagnostics.Quality,PerformanceQuality.Reconstructed),cost.Explanation);
            Add("Unrealized","区间未实现市场损益",cost.Unrealized,"金额",cost.Unrealized is null?PerformanceQuality.InsufficientData:PerformanceDiagnostics.Max(diagnostics.Quality,PerformanceQuality.Reconstructed),cost.Explanation);
            var explained=accounts.Values.Select(a=>context.Coverage[a.Id].Where(c=>c.From==effectiveFrom && c.To==request.To && c.ExplainedProfit.HasValue).ToArray()).ToArray();
            decimal? difference=null;
            if(profit.HasValue && explained.All(x=>x.Length==1)) difference=profit-valuation.SumConverted(explained.Select(x=>(x[0].ExplainedProfit!.Value,x[0].Currency,request.To)));
            Add("ReconciliationDifference","独立损益桥接差额",difference,"金额",difference is null?PerformanceQuality.InsufficientData:diagnostics.Quality,difference is null?"尚无独立结单损益桥接证据；不会以公式恒等式冒充对账。":"NAV 桥接损益与独立结单解释损益的差额，不自动摊入收益。");
            if(difference.HasValue && Math.Abs(difference.Value)>diagnostics.Tolerance(profit??0))
            {
                diagnostics.Issue("BRIDGE_DIFFERENCE","","独立损益与净值桥接不一致。","核对遗漏事件、净值和汇率；不自动调整收益。");
                // 独立证据冲突发生在桥接阶段，不能保留此前算出的收益率冒充通过验收。
                for(var i=0;i<metrics.Count;i++)if(metrics[i].Id is "Profit" or "CumulativeProfit" or "Twr" or "Xirr")
                    metrics[i]=metrics[i] with{Value=null,Quality=PerformanceQuality.InsufficientData,Explanation=metrics[i].Explanation+" 独立损益桥接差异超限，待核对。"};
            }
            return metrics;
            void Add(string id,string name,decimal? value,string unit,PerformanceQuality q,string explanation,bool annualized=false)
                =>metrics.Add(new(id,name,value,unit,value is null?PerformanceQuality.InsufficientData:q,explanation,annualized));
        }
        private PerformanceAccountResult[] BuildAccountRows()
        {
            return accounts.Values.OrderBy(a=>a.Id,StringComparer.Ordinal).Select(a=>
            {
                var start=valuation.AccountNav(a.Id,effectiveFrom,ValuationMoment.EndOfDay); var end=valuation.AccountNav(a.Id,request.To,ValuationMoment.EndOfDay);
                var selected=events.Where(e=>e.AccountId==a.Id && PerformanceCashFlowClassifier.IsFlow(e)).ToArray();
                var n=Covered(a.Id) && selected.All(e=>e.Confirmed) && !diagnostics.Issues.Any(i=>i.BlocksReturns && i.AccountAlias==a.Alias)?valuation.SumEvents(selected,e=>e.Amount):(decimal?)null;
                var snapshot=valuation.Snapshot(a.Id,request.To,ValuationMoment.EndOfDay);
                decimal? Component(string kind)=>snapshot is null || snapshot.Components.Any(c=>c.Kind==kind && c.Amount is null)?null:valuation.SumConverted(snapshot.Components.Where(c=>c.Kind==kind).Select(c=>(c.Amount!.Value,c.Currency,request.To)));
                return new PerformanceAccountResult(a.Alias,a.Broker,start,end,Component("Cash"),Component("Liability"),n,start is null || end is null || n is null || diagnostics.Issues.Any(i=>i.Code=="BRIDGE_DIFFERENCE")?null:end-start-n,request.Currency);
            }).ToArray();
        }
        private bool Covered(string id,Func<PerformanceCoverage,bool>? predicate=null)
        {
            var cursor=effectiveFrom.DayNumber;
            foreach(var c in context.Coverage[id].Where(c=>(predicate?.Invoke(c)??c.ExternalFlowsComplete) && c.To>=effectiveFrom).OrderBy(c=>c.From))
            {if(c.From.DayNumber>cursor) return false; cursor=Math.Max(cursor,c.To.DayNumber+1); if(cursor>request.To.DayNumber) return true;}
            return false;
        }
    }
}
