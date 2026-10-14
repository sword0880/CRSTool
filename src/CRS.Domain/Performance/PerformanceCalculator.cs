namespace CRS.Domain.Performance;

/// <summary>只消费冻结绩效资料；不读取税务 FIFO、不访问行情或文件。</summary>
public static class PerformanceCalculator
{
    public const string Version="Performance.v1";
    public static PerformanceResult Calculate(PerformanceData data,PerformanceRequest request,CancellationToken cancellation=default)
    {
        try {return new Evaluation(data,request,cancellation).Run();}
        catch(OverflowException) {throw new CrsException("绩效金额、汇率或复合收益超出可计算范围，请核对来源。");}
    }

    private sealed class Evaluation(PerformanceData data,PerformanceRequest request,CancellationToken cancellation)
    {
        private readonly List<PerformanceIssue> issues=[];
        private readonly List<PerformanceReconciliation> reconciliations=[];
        private readonly Dictionary<string,InvestmentAccount> accounts=[];
        private readonly Dictionary<string,decimal?> navCache=[];
        private PerformanceQuality quality=PerformanceQuality.Verified;
        private bool valuationFailed;
        private decimal? opening,ending;
        private DateOnly effectiveFrom;
        private InvestmentEvent[] events=[];
        private readonly List<PerformanceFlow> flows=[];
        private readonly List<PerformanceExposure> exposure=[];
        private string Alias(string id)=>accounts.TryGetValue(id,out var a)?a.Alias:"范围资料";
        private void Issue(string code,string account,string message,string action,PerformanceSource? source=null,bool blocks=true,string eventId="")
            =>issues.Add(new(code,Alias(account),source?.DocumentId??"",source?.RowIdentity??"",message,action,blocks,eventId));
        private void Lower(PerformanceQuality q) {if(q>quality)quality=q;}
        private static bool Currency(string value)=>value is not null && value.Length==3 && value.All(c=>c is >= 'A' and <= 'Z');
        internal PerformanceResult Run()
        {
            Validate(); effectiveFrom=request.From;
            foreach(var id in request.AccountIds.Order(StringComparer.Ordinal)) accounts.Add(id,data.Accounts.Single(a=>a.Id==id));
            if(request.SinceInception)
            {
                var starts=accounts.Values.Select(a=>data.Valuations.Where(v=>v.AccountId==a.Id && v.Moment==ValuationMoment.EndOfDay).Select(v=>(DateOnly?)v.Date).Min()).ToArray();
                if(starts.All(x=>x is not null)) effectiveFrom=starts.Max()!.Value;
                if(effectiveFrom!=request.From) Issue("TRUSTED_START","","成立以来只能从共同可用净值起点计算，未回填开户日或零净值。","提供更早完整净值和资金流，或选择共同区间。",blocks:false);
                if(effectiveFrom>request.To) throw new CrsException("共同可信起点晚于结束日期。");
            }
            opening=Nav(effectiveFrom,ValuationMoment.EndOfDay); ending=Nav(request.To,ValuationMoment.EndOfDay);
            events=PerformanceClassifications.Apply(data).Where(e=>accounts.ContainsKey(e.AccountId) && e.Date>=effectiveFrom && e.Date<=request.To)
                .Where(e=>InWindow(e)).OrderBy(e=>e.Time).ThenBy(e=>e.Id,StringComparer.Ordinal).ToArray();
            foreach(var e in events){PerformanceClassifications.ValidateDirection(e);Lower(e.Quality);}
            foreach(var a in accounts.Values)
            {
                if(!a.BaseCurrencyConfirmed)Issue("ACCOUNT_CURRENCY_UNCONFIRMED",a.Id,"账户基础币种尚未核对，原币组成仍按各自显式币种估值。","在资料补录中核对并确认账户基础币种。",blocks:false);
                if(effectiveFrom<a.TrustedFrom) Issue("INITIAL_NAV_MISSING",a.Id,"所选起点早于账户登记的可信起点。","补齐期初资料或缩小区间。");
                if(!Covered(a.Id)) Issue("CASH_FLOW_COVERAGE",a.Id,"外部资金流完整性未确认覆盖整个区间，不能按零流量处理。","导入完整结单并补录/核对期间台账。");
            }
            Classify();
            var blocked=valuationFailed || opening is null || ending is null || issues.Any(i=>i.BlocksReturns);
            var net=blocked?(decimal?)null:flows.Where(f=>!f.Internal).Sum(f=>f.Amount);
            var profit=net is null?null:ending-opening-net;
            var metrics=new List<PerformanceMetric>();
            Add("OpeningNav","期初净资产",opening,"金额",quality,"证券+现金+已确认应收-融资负债-已确认应付；保留原币和来源。");
            Add("EndingNav","期末净资产",ending,"金额",quality,"按报告日历史汇率折算；不同市场结单时点按日末口径聚合。");
            Add("NetInvestment","区间外部净投入",net,"金额",blocked?PerformanceQuality.InsufficientData:quality,"仅当前范围的外部资金和实物资产流；股息、借款、换汇不是投入。");
            Add("Profit","区间投资损益",profit,"金额",blocked?PerformanceQuality.InsufficientData:quality,"期末净资产-期初净资产-外部净投入；账户净回报，不扣未真实扣款的个人所得税。");
            decimal? cumulative=null;
            var commonStart=accounts.Values.Select(a=>data.Valuations.Where(v=>v.AccountId==a.Id && v.Moment==ValuationMoment.EndOfDay).Select(v=>(DateOnly?)v.Date).Min()).ToArray();
            if(commonStart.All(x=>x.HasValue) && commonStart.Max()==effectiveFrom) cumulative=profit;
            Add("CumulativeProfit","可信起点累计投资损益",cumulative,"金额",cumulative is null?PerformanceQuality.InsufficientData:quality,cumulative is null?"当前区间未从共同可信净值起点开始；选择成立以来重算。":"仅从共同可信起点累计，不代表开户以来。");
            var twr=blocked?new XirrResult(null,"净值、资金流或异常资料不足。"):Twr();
            Add("Twr",request.Method==ReturnMethod.Exact?"TWR（区间累计）":"TWR（日末假定重建，区间累计）",twr.Rate,"比例",twr.Rate is null?PerformanceQuality.InsufficientData:request.Method==ReturnMethod.Exact?quality:Max(quality,PerformanceQuality.Reconstructed),twr.Explanation);
            var xirr=blocked?new XirrResult(null,"期初/期末净值或外部流量资料不足。"):opening<=0 || ending<0?new(null,"零或负起始净值/负结束净值的杠杆区间不自动求解。"):XirrSolver.Solve(
                new[]{(effectiveFrom,-opening!.Value),(request.To,ending!.Value)}.Concat(flows.Where(f=>!f.Internal).Select(f=>(f.Date,-f.Amount))),cancellation);
            Add("Xirr","XIRR（资金加权年化）",xirr.Rate,"比例",xirr.Rate is null?PerformanceQuality.InsufficientData:quality,xirr.Explanation,true);
            var incomeComplete=accounts.Keys.All(id=>Covered(id,c=>c.IncomeComplete));var costsComplete=accounts.Keys.All(id=>Covered(id,c=>c.CostsComplete));
            decimal? Income(InvestmentEventKind kind)=>incomeComplete?SumEvents(events.Where(e=>e.Kind==kind),e=>e.Amount-e.Withholding):null;
            var dividend=Income(InvestmentEventKind.Dividend); var interest=Income(InvestmentEventKind.Interest);
            var withholding=SumEvents(events.Where(e=>e.Kind==InvestmentEventKind.WithholdingTax),e=>Math.Abs(e.Amount));
            Add("DividendGross","股息毛额",incomeComplete?SumEvents(events.Where(e=>e.Kind==InvestmentEventKind.Dividend),e=>e.Amount):null,"金额",quality,"须有完整收入台账；源泉税在净收入另扣，不列外部入金。");
            Add("IncomeNet","股息与利息净收入",dividend+interest-withholding,"金额",quality,"股息、利息减附带预扣税及独立预扣税事件；不包含个人最终补税。");
            Add("Fees","交易与其他费用",costsComplete?SumEvents(events.Where(e=>e.Kind is InvestmentEventKind.Fee or InvestmentEventKind.Buy or InvestmentEventKind.Sell or InvestmentEventKind.FxExchange),e=>e.Kind==InvestmentEventKind.Fee?Math.Abs(e.Amount):e.Fees):null,"金额",quality,"须有完整费用台账；成交附带费用与独立费用只保留一份来源分录。");
            Add("FinancingCost","融资利息",costsComplete?SumEvents(events.Where(e=>e.Kind==InvestmentEventKind.FinancingInterest),e=>Math.Abs(e.Amount)):null,"金额",quality,"须有完整费用台账；借入本金不是外部投入，融资利息是投资成本。");
            // 资金流/复杂事件已经阻断时，区间成本结果也不能宣称完整。
            var cost=blocked?new PerformanceCostResult(null,null,"净值、资金流或复杂事件未核对，区间成本损益暂不可计算。"):
                PerformanceCostCalculator.Calculate(data,events,accounts.Keys.ToArray(),effectiveFrom,request.To,
                (amount,currency,date)=>Convert(amount,currency,date),cancellation);
            Add("Realized","区间已实现市场损益",cost.Realized,"金额",cost.Realized is null?PerformanceQuality.InsufficientData:Max(quality,PerformanceQuality.Reconstructed),cost.Explanation);
            Add("Unrealized","区间未实现市场损益",cost.Unrealized,"金额",cost.Unrealized is null?PerformanceQuality.InsufficientData:Max(quality,PerformanceQuality.Reconstructed),cost.Explanation);
            var explained=accounts.Values.Select(a=>data.Coverage.Where(c=>c.AccountId==a.Id && c.From==effectiveFrom && c.To==request.To && c.ExplainedProfit.HasValue).ToArray()).ToArray();
            decimal? difference=null;
            if(profit.HasValue && explained.All(x=>x.Length==1)) difference=profit-SumConverted(explained.Select(x=>(x[0].ExplainedProfit!.Value,x[0].Currency,request.To)));
            Add("ReconciliationDifference","独立损益桥接差额",difference,"金额",difference is null?PerformanceQuality.InsufficientData:quality,difference is null?"尚无独立结单损益桥接证据；不会以公式恒等式冒充对账。":"NAV 桥接损益与独立结单解释损益的差额，不自动摊入收益。");
            if(difference.HasValue && Math.Abs(difference.Value)>Tolerance(profit??0))
            {
                Issue("BRIDGE_DIFFERENCE","","独立损益与净值桥接不一致。","核对遗漏事件、净值和汇率；不自动调整收益。");
                // 独立证据冲突发生在桥接阶段，不能保留此前算出的收益率冒充通过验收。
                for(var i=0;i<metrics.Count;i++)if(metrics[i].Id is "Profit" or "CumulativeProfit" or "Twr" or "Xirr")
                    metrics[i]=metrics[i] with{Value=null,Quality=PerformanceQuality.InsufficientData,Explanation=metrics[i].Explanation+" 独立损益桥接差异超限，待核对。"};
            }
            var accountRows=accounts.Values.OrderBy(a=>a.Id,StringComparer.Ordinal).Select(a=>
            {
                var start=AccountNav(a.Id,effectiveFrom,ValuationMoment.EndOfDay); var end=AccountNav(a.Id,request.To,ValuationMoment.EndOfDay);
                var selected=events.Where(e=>e.AccountId==a.Id && IsFlow(e)).ToArray();
                var n=Covered(a.Id) && selected.All(e=>e.Confirmed) && !issues.Any(i=>i.BlocksReturns && i.AccountAlias==a.Alias)?SumEvents(selected,e=>e.Amount):(decimal?)null;
                var snapshot=Snapshot(a.Id,request.To,ValuationMoment.EndOfDay);
                decimal? Component(string kind)=>snapshot is null || snapshot.Components.Any(c=>c.Kind==kind && c.Amount is null)?null:SumConverted(snapshot.Components.Where(c=>c.Kind==kind).Select(c=>(c.Amount!.Value,c.Currency,request.To)));
                return new PerformanceAccountResult(a.Alias,a.Broker,start,end,Component("Cash"),Component("Liability"),n,start is null || end is null || n is null || issues.Any(i=>i.Code=="BRIDGE_DIFFERENCE")?null:end-start-n,request.Currency);
            }).ToArray();
            return new PerformanceResult(Version,request,metrics.ToArray(),issues.Distinct().ToArray(),flows.ToArray(),accountRows,reconciliations.Distinct().ToArray(),effectiveFrom,"IntervalMarketFIFO.v1"){Exposure=exposure.ToArray()};
            void Add(string id,string name,decimal? value,string unit,PerformanceQuality q,string explanation,bool annualized=false)
                =>metrics.Add(new(id,name,value,unit,value is null?PerformanceQuality.InsufficientData:q,explanation,annualized));
        }
        private static PerformanceQuality Max(PerformanceQuality a,PerformanceQuality b)=>a>b?a:b;
        private decimal Tolerance(decimal value)=>Math.Max(request.CurrencyUnit,Math.Max(request.AbsoluteTolerance,Math.Abs(value)*request.RelativeTolerance));
        private bool Covered(string id,Func<PerformanceCoverage,bool>? predicate=null)
        {
            var cursor=effectiveFrom.DayNumber;
            foreach(var c in data.Coverage.Where(c=>c.AccountId==id && (predicate?.Invoke(c)??c.ExternalFlowsComplete) && c.To>=effectiveFrom).OrderBy(c=>c.From))
            {if(c.From.DayNumber>cursor) return false; cursor=Math.Max(cursor,c.To.DayNumber+1); if(cursor>request.To.DayNumber) return true;}
            return false;
        }
        private void Validate()
        {
            if(data.Version!=1 || request.From>=request.To || request.From.Year<2000 || request.To.Year>2100 || request.AccountIds.Length is <1 or >100
                || request.AccountIds.Distinct().Count()!=request.AccountIds.Length || !Currency(request.Currency) || !Enum.IsDefined(request.Method)
                || request.PriceFallbackDays is <0 or >7 || request.AbsoluteTolerance is <0 or >100000 || request.RelativeTolerance is <0 or >0.01m || request.CurrencyUnit is <=0 or >1)
                throw new CrsException("绩效区间、范围、币种或容差无效。");
            if(data.Accounts.Length>100 || data.Events.Length>20000 || data.Valuations.Length>20000 || data.Prices.Length>50000 || data.Fx.Length>50000 || data.Coverage.Length>10000 || data.Transfers.Length>10000 || data.Revisions.Length>20000)
                throw new CrsException("绩效资料超过单次可处理限额。");
            if(data.Accounts.GroupBy(a=>a.Id).Any(g=>g.Count()>1) || data.Events.GroupBy(e=>e.Id).Any(g=>g.Count()>1)
                || data.Valuations.GroupBy(v=>v.Id).Any(g=>g.Count()>1) || data.Fx.GroupBy(f=>f.Id).Any(g=>g.Count()>1) || data.Prices.GroupBy(p=>p.Id).Any(g=>g.Count()>1)
                || request.AccountIds.Any(id=>!data.Accounts.Any(a=>a.Id==id))) throw new CrsException("绩效资料存在重复身份或账户不存在。");
            if(data.Accounts.Any(a=>string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Alias) || a.Alias.Length>100 || !Currency(a.BaseCurrency))
                || data.Events.Any(e=>string.IsNullOrWhiteSpace(e.Id) || !Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Quality) || !Currency(e.Currency) || e.Date!=DateOnly.FromDateTime(e.Time.Date) || Math.Abs(e.Amount)>1_000_000_000_000_000m || e.Fees<0 || e.Withholding<0 || e.Source is null)
                || data.Valuations.Any(v=>string.IsNullOrWhiteSpace(v.Id) || v.Date!=DateOnly.FromDateTime(v.AsOf.Date) || v.Components.Length is <1 or >10000 || !Enum.IsDefined(v.Quality) || !Enum.IsDefined(v.Moment) || v.Source is null || v.Components.Any(c=>!Currency(c.Currency) || c.Kind is not ("Security" or "Cash" or "Receivable" or "Liability" or "Payable") || c.Quantity<0 || c.Kind is "Liability" or "Payable" && c.Amount<0))
                || data.Fx.Any(f=>f.Rate is <=0 or >1000000 || !Currency(f.From) || !Currency(f.To) || f.Source is null || !Enum.IsDefined(f.Quality))
                || data.Prices.Any(p=>p.Price<0 || !Currency(p.Currency) || p.Source is null || !Enum.IsDefined(p.Quality))
                || data.Coverage.Any(c=>c.From>c.To || c.Source is null || c.ExplainedProfit.HasValue && !Currency(c.Currency))
                || data.Transfers.Any(l=>l.DifferenceTolerance<0 || !Currency(l.ToleranceCurrency))) throw new CrsException("绩效金额、事件或估值结构无效。");
            foreach(var e in data.Events)
            {
                cancellation.ThrowIfCancellationRequested();
                PerformanceClassifications.ValidateDirection(e);
            }
        }
        private ValuationSnapshot? Snapshot(string id,DateOnly date,ValuationMoment moment,DateTimeOffset? time=null)
        {
            var candidates=data.Valuations.Where(v=>v.AccountId==id && v.Date==date && v.Moment==moment && (time is null || v.AsOf==time)).OrderBy(v=>v.Id,StringComparer.Ordinal).ToArray();
            if(candidates.Length==0) {Issue("NAV_MISSING",id,$"{date:yyyy-MM-dd} 缺少 {moment} 净值。","导入结单净值或有来源的资产/现金/负债快照。"); valuationFailed=true; return null;}
            if(candidates.Skip(1).Any(v=>v.AsOf!=candidates[0].AsOf || !v.Components.SequenceEqual(candidates[0].Components) || v.StatementNav!=candidates[0].StatementNav || v.StatementCurrency!=candidates[0].StatementCurrency))
            {Issue("NAV_CONFLICT",id,"同日、同口径存在来源或时点冲突的净值。","核对原始来源；通过修订版本明确排除错误来源。",candidates[0].Source); valuationFailed=true; return null;}
            return candidates[0];
        }
        private bool InWindow(InvestmentEvent e)
        {
            var start=data.Valuations.FirstOrDefault(v=>v.AccountId==e.AccountId && v.Date==effectiveFrom && v.Moment==ValuationMoment.EndOfDay);
            var end=data.Valuations.FirstOrDefault(v=>v.AccountId==e.AccountId && v.Date==request.To && v.Moment==ValuationMoment.EndOfDay);
            return (start is null || e.Time>start.AsOf) && (end is null || e.Time<=end.AsOf);
        }
        private decimal? Nav(DateOnly date,ValuationMoment moment,DateTimeOffset? time=null)
        {
            decimal result=0;
            foreach(var id in accounts.Keys) {var value=AccountNav(id,date,moment,time); if(value is null) return null; result+=value.Value;}
            return result;
        }
        private decimal? AccountNav(string id,DateOnly date,ValuationMoment moment,DateTimeOffset? time=null)
        {
            var key=$"{id}|{date}|{moment}|{time:O}"; if(navCache.TryGetValue(key,out var cached))return cached;
            var snapshot=Snapshot(id,date,moment,time); if(snapshot is null)return navCache[key]=null;
            if(snapshot.Quality==PerformanceQuality.InsufficientData) {Issue("NAV_QUALITY",id,"该快照明确标记资料不足，不能当完整净值。","补齐资产范围和估值依据。",snapshot.Source);valuationFailed=true;return navCache[key]=null;}
            Lower(snapshot.Quality); decimal total=0;var incomplete=false;
            foreach(var component in snapshot.Components)
            {
                cancellation.ThrowIfCancellationRequested(); var amount=component.Amount;
                if(amount is null && component.Kind=="Security")
                {
                    var prices=data.Prices.Where(p=>p.Security==component.Security && p.Market==component.Market && p.Currency==component.Currency && p.Date<=date && date.DayNumber-p.Date.DayNumber<=request.PriceFallbackDays).OrderByDescending(p=>p.Date).ToArray();
                    if(prices.Length>0 && !prices.Where(p=>p.Date==prices[0].Date).Any(p=>p.Price!=prices[0].Price || p.Adjusted || p.Quality==PerformanceQuality.InsufficientData)
                        && !data.Events.Any(e=>e.AccountId==id && e.Security==component.Security && e.Date>prices[0].Date && e.Date<=date && e.Kind is InvestmentEventKind.Split or InvestmentEventKind.Unsupported))
                    {amount=component.Quantity*prices[0].Price; Lower(prices[0].Date==date?Max(prices[0].Quality,PerformanceQuality.Reconstructed):PerformanceQuality.Estimated); if(prices[0].Date!=date)Issue("PRICE_FALLBACK",id,$"采用 {prices[0].Date:yyyy-MM-dd} 价格回退。","补充报告日价格以提高可靠性。",prices[0].Source,false);}
                }
                if(amount is null) {exposure.Add(new(Alias(id),date,component.Kind,component.Currency,null,null,snapshot.Source.DocumentId,snapshot.Source.RowIdentity,component.Security,component.Market,component.Quantity));Issue("PRICE_MISSING",id,$"{date:yyyy-MM-dd} {component.Security}/{component.Market} {component.Currency} 资产金额或可用价格缺失/冲突。","补齐该日资产原币金额或可靠行情。",snapshot.Source); valuationFailed=true;incomplete=true;continue;}
                if(Math.Abs(amount.Value)>1_000_000_000_000_000m)throw new CrsException("单项资产金额超过可计算限额。");
                var converted=Convert(amount.Value,component.Currency,date);
                // 展示原币与折算组成，缺失金额保持空值，不能伪装成零。
                exposure.Add(new(Alias(id),date,component.Kind,component.Currency,amount,converted,snapshot.Source.DocumentId,snapshot.Source.RowIdentity,component.Security,component.Market,component.Quantity));
                if(converted is null){valuationFailed=true;incomplete=true;continue;}
                total+=(component.Kind is "Liability" or "Payable"?-1:1)*converted.Value;
            }
            // 一项缺失不能隐藏其他原币组成，但不完整合计仍必须不可计算。
            if(incomplete)return navCache[key]=null;
            if(snapshot.StatementNav is { } stated)
            {
                var broker=Convert(stated,snapshot.StatementCurrency,date);
                if(broker is not null) {var tolerance=Tolerance(broker.Value); var diff=total-broker.Value; var matched=Math.Abs(diff)<=tolerance;
                    reconciliations.Add(new(Alias(id),date,total,broker.Value,diff,tolerance,matched));
                    if(!matched){Lower(PerformanceQuality.Estimated);Issue("NAV_RECONCILIATION",id,"资产组成与结单净值不一致。","核对负现金、融资负债、应收应付及汇率。",snapshot.Source);}
                    if(!snapshot.Reviewed)Lower(PerformanceQuality.Reconstructed);
                }
            }
            else Lower(Max(snapshot.Quality,PerformanceQuality.Reconstructed));
            return navCache[key]=total;
        }
        private decimal? Convert(decimal amount,string currency,DateOnly date)
        {
            if(currency==request.Currency)return amount;
            decimal? Rate(string from,string to)
            {
                var quotes=data.Fx.Where(f=>f.From==from && f.To==to && f.Date==date).ToArray();
                if(quotes.Length==0)return null;
                if(quotes.Any(f=>f.Quality==PerformanceQuality.InsufficientData)) {Issue("FX_QUALITY","","汇率明确标记资料不足，不能用于估值。","补齐可靠汇率依据。",quotes[0].Source);return null;}
                if(quotes.Any(f=>f.Rate!=quotes[0].Rate)) {Issue("FX_CONFLICT","","同日货币对汇率来源冲突。","核对汇率并修订来源版本。",quotes[0].Source);return null;}
                foreach(var f in quotes)Lower(f.Quality); return quotes[0].Rate;
            }
            var rate=Rate(currency,request.Currency); if(rate is null) {var inverse=Rate(request.Currency,currency); if(inverse is not null)rate=1/inverse;}
            if(rate is null && currency!="CNY" && request.Currency!="CNY")
            {
                var a=Rate(currency,"CNY"); var b=Rate(request.Currency,"CNY"); if(a is not null && b is not null)rate=a/b;
            }
            if(rate is null) {Issue("FX_MISSING","",$"{date:yyyy-MM-dd} 缺少 {currency}/{request.Currency} 同日历史汇率。","离线导入估值日汇率，不使用年度税务汇率或实时替代值。"); return null;}
            return amount*rate;
        }
        private decimal? SumConverted(IEnumerable<(decimal Amount,string Currency,DateOnly Date)> rows)
        {decimal sum=0; foreach(var r in rows){var v=Convert(r.Amount,r.Currency,r.Date); if(v is null)return null;sum+=v.Value;}return sum;}
        private decimal? SumEvents(IEnumerable<InvestmentEvent> rows,Func<InvestmentEvent,decimal> amount)=>SumConverted(rows.Select(e=>(amount(e),e.Currency,e.Date)));
        private static bool IsFlow(InvestmentEvent e)=>e.Kind is InvestmentEventKind.Deposit or InvestmentEventKind.Withdrawal or InvestmentEventKind.CashTransfer or InvestmentEventKind.SecurityTransfer;
        private void Classify()
        {
            var linked=new HashSet<string>();
            var effectiveEvents=PerformanceClassifications.Apply(data);
            foreach(var link in data.Transfers.Where(l=>l.Confirmed))
            {
                var outgoing=effectiveEvents.SingleOrDefault(e=>e.Id==link.OutEventId); var incoming=effectiveEvents.SingleOrDefault(e=>e.Id==link.InEventId);
                if(outgoing is null || incoming is null || outgoing.AccountId==incoming.AccountId || outgoing.Amount>=0 || incoming.Amount<=0
                    || outgoing.Kind!=incoming.Kind || outgoing.Kind is not (InvestmentEventKind.CashTransfer or InvestmentEventKind.SecurityTransfer)
                    || outgoing.Kind==InvestmentEventKind.SecurityTransfer && (outgoing.Security!=incoming.Security || outgoing.Market!=incoming.Market || Math.Abs(outgoing.Quantity)!=Math.Abs(incoming.Quantity)))
                {Issue("TRANSFER_INVALID","","已确认的转账关联身份、方向或证券数量不一致。","重新选择两侧原始事件并核对。");continue;}
                if(!accounts.ContainsKey(outgoing.AccountId) || !accounts.ContainsKey(incoming.AccountId))continue;
                if(!events.Any(e=>e.Id==outgoing.Id) || !events.Any(e=>e.Id==incoming.Id)) {Issue("TRANSFER_IN_TRANSIT",outgoing.AccountId,"内部转账一侧在统计区间之外，缺少在途估值。","调整区间或补充在途资产/流量证据。",outgoing.Source);continue;}
                var a=Convert(outgoing.Amount,outgoing.Currency,outgoing.Date);var b=Convert(incoming.Amount,incoming.Currency,incoming.Date);
                if(a is null || b is null)continue;
                // 容差按确认时币种冻结，换报告币种须用转账日汇率重新折算。
                var tolerance=link.DifferenceTolerance==0?0:Convert(link.DifferenceTolerance,link.ToleranceCurrency,outgoing.Date);
                if(tolerance is null)continue;
                if(Math.Abs(a.Value+b.Value)>tolerance) {Issue("TRANSFER_DIFFERENCE",outgoing.AccountId,"两侧折算差额超出已确认容差。","解释汇率及费用差异，不能静默消除。",outgoing.Source);continue;}
                if(!linked.Add(outgoing.Id) || !linked.Add(incoming.Id)) {Issue("TRANSFER_REUSED",outgoing.AccountId,"同一事件被多次匹配。","每个转入/转出只允许一组关联。",outgoing.Source);continue;}
                flows.Add(new(outgoing.Id,Alias(outgoing.AccountId),outgoing.Date,outgoing.Time,a.Value,true,FormattableString.Invariant($"内部转出；差额 {a+b} {request.Currency} 单列，未当外部投入。")));
                flows.Add(new(incoming.Id,Alias(incoming.AccountId),incoming.Date,incoming.Time,b.Value,true,"内部转入；只在当前范围同时包含两侧时消除。"));
            }
            foreach(var e in events)
            {
                cancellation.ThrowIfCancellationRequested();
                if(e.Quality==PerformanceQuality.InsufficientData){Issue("EVENT_QUALITY",e.AccountId,"事件来源明确标记资料不足。","补齐事件证据后再计算。",e.Source,true,e.Id);continue;}
                if(e.Kind is InvestmentEventKind.Unknown or InvestmentEventKind.Unsupported) {Issue("UNKNOWN_EVENT",e.AccountId,"未识别资金或复杂资产事件，不允许静默忽略。","查看来源并以有原因的分类修订处理。",e.Source,true,e.Id);continue;}
                if(!IsFlow(e))continue;
                if(linked.Contains(e.Id))continue;
                var link=data.Transfers.FirstOrDefault(l=>l.Confirmed && (l.OutEventId==e.Id || l.InEventId==e.Id));
                if(!e.Confirmed || e.Kind is InvestmentEventKind.CashTransfer or InvestmentEventKind.SecurityTransfer && link is null)
                {Issue("TRANSFER_UNMATCHED",e.AccountId,"外部流量未确认或转仓/转账尚未匹配。","确认外部边界或关联两侧事件；不把未知项当零。",e.Source,true,e.Id);continue;}
                var value=Convert(e.Amount,e.Currency,e.Date); if(value is not null)flows.Add(new(e.Id,Alias(e.AccountId),e.Date,e.Time,value.Value,false,"所选范围外部流量；证券实物按确认市值计入。"));
            }
        }
        private XirrResult Twr()
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
                var starts=accounts.Keys.Select(id=>data.Valuations.First(v=>v.AccountId==id && v.Date==effectiveFrom && v.Moment==ValuationMoment.EndOfDay).AsOf).Distinct().Count();
                var ends=accounts.Keys.Select(id=>data.Valuations.First(v=>v.AccountId==id && v.Date==request.To && v.Moment==ValuationMoment.EndOfDay).AsOf).Distinct().Count();
                if(starts!=1 || ends!=1 || data.Valuations.Any(v=>accounts.ContainsKey(v.AccountId) && (v.Date==effectiveFrom || v.Date==request.To) && v.Moment==ValuationMoment.EndOfDay && !v.TimeVerified))return new(null,"跨市场快照时点未确认或不是同一时点，不能声明精确 TWR；可明确选择日末假定重建。");
                foreach(var group in flows.Where(f=>!f.Internal).GroupBy(f=>f.Time).OrderBy(g=>g.Key))
                {
                    var date=group.First().Date; var before=Nav(date,ValuationMoment.BeforeFlow,group.Key); var after=Nav(date,ValuationMoment.AfterFlow,group.Key);
                    if(before is null || after is null || previous<=0 || before<0 || after<=0 || data.Valuations.Any(v=>accounts.ContainsKey(v.AccountId) && v.AsOf==group.Key && v.Moment!=ValuationMoment.EndOfDay && !v.TimeVerified))return new(null,"缺少已确认流量前后同一时点净值，或出现零/负分母。");
                    if(Math.Abs(after.Value-before.Value-group.Sum(f=>f.Amount))>Tolerance(after.Value))return new(null,"流量前后净值与外部现金/实物流量不一致。");
                    factor*=before.Value/previous;previous=after.Value;
                }
            }
            else
            {
                // 明确采用日末流量假定，不能将该重建结果命名为精确 TWR。
                for(var day=effectiveFrom.AddDays(1);day<=request.To;day=day.AddDays(1))
                {
                    cancellation.ThrowIfCancellationRequested();var current=Nav(day,ValuationMoment.EndOfDay);
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
}
