namespace CRS.Domain.Performance;

/// <summary>修订只覆盖分类，不改原始金额、成交或来源；所有边界检查使用同一有效分类。</summary>
public static class PerformanceClassifications
{
    // 分类修订不能绕过原币方向或证券数量约束；导入与修订后的计算共用此门槛。
    public static void ValidateDirection(InvestmentEvent e)
    {
        if(!Enum.IsDefined(e.Kind) || e.Kind==InvestmentEventKind.Deposit && e.Amount<0 || e.Kind==InvestmentEventKind.Withdrawal && e.Amount>0
            || e.Kind==InvestmentEventKind.Buy && (e.Amount>0 || e.Quantity<=0) || e.Kind==InvestmentEventKind.Sell && (e.Amount<0 || e.Quantity<=0)
            || e.Kind==InvestmentEventKind.SecurityTransfer && (e.Amount==0 || e.Quantity==0 || string.IsNullOrWhiteSpace(e.Security) || string.IsNullOrWhiteSpace(e.Market))
            || e.Kind==InvestmentEventKind.Split && e.SplitRatio<=0)throw new CrsException("分类与金额方向、证券数量或拆股比例不一致，请补齐原始依据。");
    }
    public static InvestmentEvent[] Apply(PerformanceData data)
    {
        var revisions=data.Revisions.OrderBy(r=>r.CreatedAt).ThenBy(r=>r.Id,StringComparer.Ordinal).GroupBy(r=>r.EventId).ToDictionary(g=>g.Key,g=>g.Last());
        // 未确认修订保留在台账，计算中仍为未知项，不能改成股息/借款绕过边界检查。
        return data.Events.Select(e=>revisions.TryGetValue(e.Id,out var r)?e with{Kind=r.Confirmed?r.Kind:InvestmentEventKind.Unknown,Confirmed=r.Confirmed}:e).ToArray();
    }
}
