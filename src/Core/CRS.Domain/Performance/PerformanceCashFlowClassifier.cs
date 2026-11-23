namespace CRS.Domain.Performance;

/// <summary>范围内外资金流与转仓匹配；不计算收益率或税务成本。</summary>
internal sealed class PerformanceCashFlowClassifier(PerformanceCalculationContext context,PerformanceDiagnostics diagnostics,PerformanceValuationEngine valuation)
{
    private PerformanceData data=>context.Data;
    private PerformanceRequest request=>context.Request;
    private CancellationToken cancellation=>context.Cancellation;
    private IReadOnlyDictionary<string,InvestmentAccount> accounts=>context.Accounts;
    internal static bool IsFlow(InvestmentEvent e)=>e.Kind is InvestmentEventKind.Deposit or InvestmentEventKind.Withdrawal or InvestmentEventKind.CashTransfer or InvestmentEventKind.SecurityTransfer;
    internal List<PerformanceFlow> Classify(InvestmentEvent[] events)
    {
        var linked=new HashSet<string>();var flows=new List<PerformanceFlow>();var eventIds=events.Select(e=>e.Id).ToHashSet();
        var effectiveEvents=context.EffectiveEvents;
        foreach(var link in data.Transfers.Where(l=>l.Confirmed))
        {
            var outgoing=effectiveEvents.GetValueOrDefault(link.OutEventId); var incoming=effectiveEvents.GetValueOrDefault(link.InEventId);
            if(outgoing is null || incoming is null || outgoing.AccountId==incoming.AccountId || outgoing.Amount>=0 || incoming.Amount<=0
                || outgoing.Kind!=incoming.Kind || outgoing.Kind is not (InvestmentEventKind.CashTransfer or InvestmentEventKind.SecurityTransfer)
                || outgoing.Kind==InvestmentEventKind.SecurityTransfer && (outgoing.Security!=incoming.Security || outgoing.Market!=incoming.Market || Math.Abs(outgoing.Quantity)!=Math.Abs(incoming.Quantity)))
            {diagnostics.Issue("TRANSFER_INVALID","","已确认的转账关联身份、方向或证券数量不一致。","重新选择两侧原始事件并核对。");continue;}
            if(!accounts.ContainsKey(outgoing.AccountId) || !accounts.ContainsKey(incoming.AccountId))continue;
            if(!eventIds.Contains(outgoing.Id) || !eventIds.Contains(incoming.Id)) {diagnostics.Issue("TRANSFER_IN_TRANSIT",outgoing.AccountId,"内部转账一侧在统计区间之外，缺少在途估值。","调整区间或补充在途资产/流量证据。",outgoing.Source);continue;}
            var a=valuation.Convert(outgoing.Amount,outgoing.Currency,outgoing.Date);var b=valuation.Convert(incoming.Amount,incoming.Currency,incoming.Date);
            if(a is null || b is null)continue;
            // 容差按确认时币种冻结，换报告币种须用转账日汇率重新折算。
            var tolerance=link.DifferenceTolerance==0?0:valuation.Convert(link.DifferenceTolerance,link.ToleranceCurrency,outgoing.Date);
            if(tolerance is null)continue;
            if(Math.Abs(a.Value+b.Value)>tolerance) {diagnostics.Issue("TRANSFER_DIFFERENCE",outgoing.AccountId,"两侧折算差额超出已确认容差。","解释汇率及费用差异，不能静默消除。",outgoing.Source);continue;}
            if(!linked.Add(outgoing.Id) || !linked.Add(incoming.Id)) {diagnostics.Issue("TRANSFER_REUSED",outgoing.AccountId,"同一事件被多次匹配。","每个转入/转出只允许一组关联。",outgoing.Source);continue;}
            flows.Add(new(outgoing.Id,diagnostics.Alias(outgoing.AccountId),outgoing.Date,outgoing.Time,a.Value,true,FormattableString.Invariant($"内部转出；差额 {a+b} {request.Currency} 单列，未当外部投入。")));
            flows.Add(new(incoming.Id,diagnostics.Alias(incoming.AccountId),incoming.Date,incoming.Time,b.Value,true,"内部转入；只在当前范围同时包含两侧时消除。"));
        }
        foreach(var e in events)
        {
            cancellation.ThrowIfCancellationRequested();
            if(e.Quality==PerformanceQuality.InsufficientData){diagnostics.Issue("EVENT_QUALITY",e.AccountId,"事件来源明确标记资料不足。","补齐事件证据后再计算。",e.Source,true,e.Id);continue;}
            if(e.Kind is InvestmentEventKind.Unknown or InvestmentEventKind.Unsupported) {diagnostics.Issue("UNKNOWN_EVENT",e.AccountId,"未识别资金或复杂资产事件，不允许静默忽略。","查看来源并以有原因的分类修订处理。",e.Source,true,e.Id);continue;}
            if(!IsFlow(e))continue;
            if(linked.Contains(e.Id))continue;
            var link=context.ConfirmedTransfers[e.Id].FirstOrDefault();
            if(!e.Confirmed || e.Kind is InvestmentEventKind.CashTransfer or InvestmentEventKind.SecurityTransfer && link is null)
            {diagnostics.Issue("TRANSFER_UNMATCHED",e.AccountId,"外部流量未确认或转仓/转账尚未匹配。","确认外部边界或关联两侧事件；不把未知项当零。",e.Source,true,e.Id);continue;}
            var value=valuation.Convert(e.Amount,e.Currency,e.Date); if(value is not null)flows.Add(new(e.Id,diagnostics.Alias(e.AccountId),e.Date,e.Time,value.Value,false,"所选范围外部流量；证券实物按确认市值计入。"));
        }
        return flows;
    }
}
