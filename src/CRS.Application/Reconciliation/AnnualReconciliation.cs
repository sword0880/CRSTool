namespace CRS.Application;

/// <summary>比较计算结果与券商事实，记录持仓、卖出收益和现金差异。</summary>
internal static class AnnualReconciliation
{
    public static bool CheckPositions(ImportData data, DateOnly end, string[] accounts,
        FifoEngine engine, List<Issue> issues, bool positionReconciled, CancellationToken cancellation)
    {
        foreach (var account in accounts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!data.Sources.Any(s => !s.Opening && s.Account == account && s.End == end && s.HasPositions)) continue;
            var expected = data.Positions.Where(p => p.Date == end && p.Key.Account == account).ToDictionary(p => p.Key, p => p.Quantity);
            foreach (var key in expected.Keys.Concat(engine.EndingQuantities.Keys.Where(k => k.Account == account)).Distinct())
                if (expected.GetValueOrDefault(key) != engine.EndingQuantities.GetValueOrDefault(key) || engine.Uncertain.Contains(key))
                {
                    positionReconciled = false;
                    issues.Add(new("POSITION_MISMATCH", "计算期末数量与券商持仓不一致，或成本链不可信。", account, Currency: key.Currency, Instrument: key.Instrument, AffectsCost: true));
                }
        }
        return positionReconciled;
    }

    public static (List<PnlReconciliation> Pnl, List<CashReconciliation> Cash) CheckSalesAndCash(
        ImportData data, int year, DateOnly begin, DateOnly end, List<Match> matches,
        List<Issue> issues, CancellationToken cancellation)
    {
        var pnlChecks = new List<PnlReconciliation>();
        // 每笔卖出仅访问自身匹配批次，避免对整个年度底稿重复扫描。
        var matchesBySale = matches.ToLookup(m => (m.Key, m.SellId));
        foreach (var t in data.ReportedSales.Where(t => t.Time.Year == year))
        {
            cancellation.ThrowIfCancellationRequested();
            var rows = matchesBySale[(t.Key, t.Id)].ToList();
            decimal? calculated = rows.Sum(m => m.Quantity) == t.Quantity ? rows.Sum(m => m.Gain) : null;
            var difference = calculated - t.ReportedPnl;
            var status = calculated is null ? "成本不完整" : t.ReportedPnl is null ? "缺少券商收益" : Math.Abs(difference!.Value) <= .02m ? "一致" : "存在差异";
            pnlChecks.Add(new(t.Key.Account, t.Symbol, t.Key.Currency, t.Id, calculated, t.ReportedPnl, difference, status));
            if (status != "一致") issues.Add(new("PNL_REVIEW", $"卖出收益核对：{status}。", t.Key.Account, t.Symbol, t.Key.Currency, File: t.File, RecordId: t.Id));
        }
        var cashChecks = data.CashChecks.Where(c => c.Start <= end && c.End >= begin).ToList();
        foreach (var c in cashChecks.Where(c => c.Status != "一致")) issues.Add(new("CASH_REVIEW", $"现金余额{c.Status}：{c.Reason}", c.Account, Currency: c.Currency, File: c.File));
        return (pnlChecks, cashChecks);
    }
}
