namespace CRS.Domain;

/// <summary>计算和规范化快照共用稳定顺序；文件名只消除输入顺序差异，不证明成交先后。</summary>
public static class FifoOrdering
{
    public static IOrderedEnumerable<Trade> Trades(IEnumerable<Trade> trades) => trades
        .OrderBy(t => t.Time).ThenBy(t => t.Key.Account, StringComparer.Ordinal)
        .ThenBy(t => t.Key.Broker, StringComparer.Ordinal).ThenBy(t => t.Key.Instrument, StringComparer.Ordinal)
        .ThenBy(t => t.Key.Market, StringComparer.Ordinal).ThenBy(t => t.Key.Currency, StringComparer.Ordinal)
        .ThenBy(t => t.File, StringComparer.Ordinal).ThenBy(t => t.Row).ThenBy(t => t.Id, StringComparer.Ordinal)
        .ThenBy(t => t.Side, StringComparer.Ordinal).ThenBy(t => t.Quantity).ThenBy(t => t.Price)
        .ThenBy(t => t.Gross).ThenBy(t => t.Fee).ThenBy(t => t.Net)
        .ThenBy(t => t.Symbol, StringComparer.Ordinal).ThenBy(t => t.Asset, StringComparer.Ordinal)
        .ThenBy(t => t.HasOffset).ThenBy(t => t.ReportedPnl).ThenBy(t => t.Time.Offset);

    /// <summary>同一买入时刻按完整批次来源和余额稳定排序。</summary>
    public static IOrderedEnumerable<CostLot> Lots(IEnumerable<CostLot> lots) => lots
        .OrderBy(l => l.BuyTime).ThenBy(l => l.Key.Account, StringComparer.Ordinal)
        .ThenBy(l => l.Key.Broker, StringComparer.Ordinal).ThenBy(l => l.Key.Instrument, StringComparer.Ordinal)
        .ThenBy(l => l.Key.Market, StringComparer.Ordinal).ThenBy(l => l.Key.Currency, StringComparer.Ordinal)
        .ThenBy(l => l.SourceFile, StringComparer.Ordinal).ThenBy(l => l.RecordId, StringComparer.Ordinal)
        .ThenBy(l => l.Quantity).ThenBy(l => l.Cost).ThenBy(l => l.Fee).ThenBy(l => l.CostBasisMode)
        .ThenBy(l => l.Symbol, StringComparer.Ordinal).ThenBy(l => l.HasOffset).ThenBy(l => l.BuyTime.Offset);
}
