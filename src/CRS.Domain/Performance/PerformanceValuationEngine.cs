namespace CRS.Domain.Performance;

/// <summary>估值、价格/汇率选择及净值核验；只消费当前计算上下文。</summary>
internal sealed class PerformanceValuationEngine(PerformanceCalculationContext context,PerformanceDiagnostics diagnostics)
{
    private PerformanceRequest request=>context.Request;
    private CancellationToken cancellation=>context.Cancellation;
    private IReadOnlyDictionary<string,InvestmentAccount> accounts=>context.Accounts;
    private readonly Dictionary<string,decimal?> navCache=[];
    private bool valuationFailed;
    internal bool Failed=>valuationFailed;
    internal ValuationSnapshot? Snapshot(string id,DateOnly date,ValuationMoment moment,DateTimeOffset? time=null)
    {
        var candidates=context.Valuations[(id,date,moment)].Where(v=>time is null || v.AsOf==time).OrderBy(v=>v.Id,StringComparer.Ordinal).ToArray();
        if(candidates.Length==0) {diagnostics.Issue("NAV_MISSING",id,$"{date:yyyy-MM-dd} 缺少 {moment} 净值。","导入结单净值或有来源的资产/现金/负债快照。"); valuationFailed=true; return null;}
        if(candidates.Skip(1).Any(v=>v.AsOf!=candidates[0].AsOf || !v.Components.SequenceEqual(candidates[0].Components) || v.StatementNav!=candidates[0].StatementNav || v.StatementCurrency!=candidates[0].StatementCurrency))
        {diagnostics.Issue("NAV_CONFLICT",id,"同日、同口径存在来源或时点冲突的净值。","核对原始来源；通过修订版本明确排除错误来源。",candidates[0].Source); valuationFailed=true; return null;}
        return candidates[0];
    }
    internal bool InWindow(InvestmentEvent e,DateOnly from)
    {
        var start=context.Valuations[(e.AccountId,from,ValuationMoment.EndOfDay)].FirstOrDefault();
        var end=context.Valuations[(e.AccountId,request.To,ValuationMoment.EndOfDay)].FirstOrDefault();
        return (start is null || e.Time>start.AsOf) && (end is null || e.Time<=end.AsOf);
    }
    internal decimal? Nav(DateOnly date,ValuationMoment moment,DateTimeOffset? time=null)
    {
        decimal result=0;
        foreach(var id in accounts.Keys) {var value=AccountNav(id,date,moment,time); if(value is null) return null; result+=value.Value;}
        return result;
    }
    internal decimal? AccountNav(string id,DateOnly date,ValuationMoment moment,DateTimeOffset? time=null)
    {
        var key=$"{id}|{date}|{moment}|{time:O}"; if(navCache.TryGetValue(key,out var cached))return cached;
        var snapshot=Snapshot(id,date,moment,time); if(snapshot is null)return navCache[key]=null;
        if(snapshot.Quality==PerformanceQuality.InsufficientData) {diagnostics.Issue("NAV_QUALITY",id,"该快照明确标记资料不足，不能当完整净值。","补齐资产范围和估值依据。",snapshot.Source);valuationFailed=true;return navCache[key]=null;}
        diagnostics.Lower(snapshot.Quality); decimal total=0;var incomplete=false;
        foreach(var component in snapshot.Components)
        {
            cancellation.ThrowIfCancellationRequested(); var amount=component.Amount;
            if(amount is null && component.Kind=="Security")
            {
                var prices=context.Prices[(component.Security,component.Market,component.Currency)].Where(p=>p.Date<=date && date.DayNumber-p.Date.DayNumber<=request.PriceFallbackDays).OrderByDescending(p=>p.Date).ToArray();
                if(prices.Length>0 && !prices.Where(p=>p.Date==prices[0].Date).Any(p=>p.Price!=prices[0].Price || p.Adjusted || p.Quality==PerformanceQuality.InsufficientData)
                    && !context.SecurityEvents[(id,component.Security)].Any(e=>e.Date>prices[0].Date && e.Date<=date && e.Kind is InvestmentEventKind.Split or InvestmentEventKind.Unsupported))
                {amount=component.Quantity*prices[0].Price; diagnostics.Lower(prices[0].Date==date?PerformanceDiagnostics.Max(prices[0].Quality,PerformanceQuality.Reconstructed):PerformanceQuality.Estimated); if(prices[0].Date!=date)diagnostics.Issue("PRICE_FALLBACK",id,$"采用 {prices[0].Date:yyyy-MM-dd} 价格回退。","补充报告日价格以提高可靠性。",prices[0].Source,false);}
            }
            if(amount is null) {diagnostics.Exposure.Add(new(diagnostics.Alias(id),date,component.Kind,component.Currency,null,null,snapshot.Source.DocumentId,snapshot.Source.RowIdentity,component.Security,component.Market,component.Quantity));diagnostics.Issue("PRICE_MISSING",id,$"{date:yyyy-MM-dd} {component.Security}/{component.Market} {component.Currency} 资产金额或可用价格缺失/冲突。","补齐该日资产原币金额或可靠行情。",snapshot.Source); valuationFailed=true;incomplete=true;continue;}
            if(Math.Abs(amount.Value)>1_000_000_000_000_000m)throw new CrsException("单项资产金额超过可计算限额。");
            var converted=Convert(amount.Value,component.Currency,date);
            // 展示原币与折算组成，缺失金额保持空值，不能伪装成零。
            diagnostics.Exposure.Add(new(diagnostics.Alias(id),date,component.Kind,component.Currency,amount,converted,snapshot.Source.DocumentId,snapshot.Source.RowIdentity,component.Security,component.Market,component.Quantity));
            if(converted is null){valuationFailed=true;incomplete=true;continue;}
            total+=(component.Kind is "Liability" or "Payable"?-1:1)*converted.Value;
        }
        // 一项缺失不能隐藏其他原币组成，但不完整合计仍必须不可计算。
        if(incomplete)return navCache[key]=null;
        if(snapshot.StatementNav is { } stated)
        {
            var broker=Convert(stated,snapshot.StatementCurrency,date);
            if(broker is not null) {var tolerance=diagnostics.Tolerance(broker.Value); var diff=total-broker.Value; var matched=Math.Abs(diff)<=tolerance;
                diagnostics.Reconciliations.Add(new(diagnostics.Alias(id),date,total,broker.Value,diff,tolerance,matched));
                if(!matched){diagnostics.Lower(PerformanceQuality.Estimated);diagnostics.Issue("NAV_RECONCILIATION",id,"资产组成与结单净值不一致。","核对负现金、融资负债、应收应付及汇率。",snapshot.Source);}
                if(!snapshot.Reviewed)diagnostics.Lower(PerformanceQuality.Reconstructed);
            }
        }
        else diagnostics.Lower(PerformanceDiagnostics.Max(snapshot.Quality,PerformanceQuality.Reconstructed));
        return navCache[key]=total;
    }
    internal decimal? Convert(decimal amount,string currency,DateOnly date)
    {
        if(currency==request.Currency)return amount;
        decimal? Rate(string from,string to)
        {
            var quotes=context.Fx[(from,to,date)].ToArray();
            if(quotes.Length==0)return null;
            if(quotes.Any(f=>f.Quality==PerformanceQuality.InsufficientData)) {diagnostics.Issue("FX_QUALITY","","汇率明确标记资料不足，不能用于估值。","补齐可靠汇率依据。",quotes[0].Source);return null;}
            if(quotes.Any(f=>f.Rate!=quotes[0].Rate)) {diagnostics.Issue("FX_CONFLICT","","同日货币对汇率来源冲突。","核对汇率并修订来源版本。",quotes[0].Source);return null;}
            foreach(var f in quotes)diagnostics.Lower(f.Quality); return quotes[0].Rate;
        }
        var rate=Rate(currency,request.Currency); if(rate is null) {var inverse=Rate(request.Currency,currency); if(inverse is not null)rate=1/inverse;}
        if(rate is null && currency!="CNY" && request.Currency!="CNY")
        {
            var a=Rate(currency,"CNY"); var b=Rate(request.Currency,"CNY"); if(a is not null && b is not null)rate=a/b;
        }
        if(rate is null) {diagnostics.Issue("FX_MISSING","",$"{date:yyyy-MM-dd} 缺少 {currency}/{request.Currency} 同日历史汇率。","离线导入估值日汇率，不使用年度税务汇率或实时替代值。"); return null;}
        return amount*rate;
    }
    internal decimal? SumConverted(IEnumerable<(decimal Amount,string Currency,DateOnly Date)> rows)
    {decimal sum=0; foreach(var r in rows){var v=Convert(r.Amount,r.Currency,r.Date); if(v is null)return null;sum+=v.Value;}return sum;}
    internal decimal? SumEvents(IEnumerable<InvestmentEvent> rows,Func<InvestmentEvent,decimal> amount)=>SumConverted(rows.Select(e=>(amount(e),e.Currency,e.Date)));
}
