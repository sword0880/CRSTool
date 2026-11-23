namespace CRS.Application;

/// <summary>比较计算结果与券商事实，记录持仓、卖出收益和现金差异。</summary>
internal static class AnnualReconciliation
{
    internal static bool HasFutuAnnualScope(ImportData data, int year)
    {
        if (data.Broker != "FUTU" || !TaxImportProvenance.UsesFutuAutomaticProjection(data.ImportProvenance)) return false;
        var begin = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
        var activity = data.Sources.Where(s => s.Purpose == "AnnualActivity" && s.Start == begin && s.End == end)
            .Select(s => s.Account).ToHashSet(StringComparer.Ordinal);
        var income = data.AnnualIncome.Where(i => i.Year == year).Select(i => i.Account);
        return activity.Count > 0 && activity.SetEquals(income);
    }

    internal static FifoAccountTransfer[] MatchFutuAccountUpgrades(ImportData data, int year, bool scopeConfirmed,
        bool hasOpeningCarry, List<Issue> issues)
    {
        if (data.Broker != "FUTU" || !scopeConfirmed || !TaxImportProvenance.UsesFutuAutomaticProjection(data.ImportProvenance))
            return [];
        // 期初结转已经包含以前年度的库存变化。
        var movements = data.SecurityMovements.Where(m => m.Date.Year <= year && (!hasOpeningCarry || m.Date.Year == year)).ToArray();
        var activityByDay = movements.ToLookup(m => (m.Key, m.Date));
        var tradeDays = data.Trades.Select(t => (t.Key, Date: DateOnly.FromDateTime(t.Time.Date))).ToHashSet();
        var matched = new List<FifoAccountTransfer>();
        var evidence = new HashSet<(string File, string RecordId)>();
        // 同份年度报表、同日、同一证券和币种的升级记录须有唯一且等量的转出。
        foreach (var group in movements.GroupBy(m => (m.File, m.Date, m.Key.Instrument, m.Key.Market, m.Key.Currency, Math.Abs(m.Quantity))))
        {
            var rows = group.Take(3).ToArray();
            if (rows.Length != 2) continue;
            var incoming = rows.FirstOrDefault(m => m is { Type: "账户升级", Direction: "In" });
            var outgoing = rows.FirstOrDefault(m => m is { Type: "资产进出", Direction: "Out" });
            if (incoming is null || outgoing is null || incoming.Quantity == 0 ||
                incoming.Key.Broker != "FUTU" || outgoing.Key.Broker != "FUTU" ||
                incoming.Key.Account == outgoing.Key.Account || incoming.Key.Instrument == "" || incoming.Key.Market == "" ||
                activityByDay[(incoming.Key, incoming.Date)].Count() != 1 ||
                activityByDay[(outgoing.Key, outgoing.Date)].Count() != 1 ||
                tradeDays.Contains((incoming.Key, incoming.Date)) || tradeDays.Contains((outgoing.Key, outgoing.Date))) continue;
            matched.Add(new(outgoing.Key, incoming.Key, incoming.Date, Math.Abs(incoming.Quantity), incoming.File));
            evidence.Add((incoming.File, $"{incoming.Sheet}:{incoming.Row}"));
            evidence.Add((outgoing.File, $"{outgoing.Sheet}:{outgoing.Row}"));
        }
        issues.RemoveAll(i => i.Code == "UNSUPPORTED_TRANSFER" && evidence.Contains((i.File, i.RecordId)));
        return matched.ToArray();
    }

    public static bool CheckPositions(ImportData data, DateOnly end, string[] accounts,
        FifoEngine engine, List<Issue> issues, bool positionReconciled, CancellationToken cancellation)
    {
        foreach (var account in accounts)
        {
            var accountEnd=AccountPeriodPolicy.Window(data,account,new DateOnly(end.Year,1,1),end).End;
            cancellation.ThrowIfCancellationRequested();
            if (!data.Sources.Any(s => !s.Opening && s.Account == account && s.End == accountEnd && s.HasPositions)) continue;
            var expected = data.Positions.Where(p => p.Date == accountEnd && p.Key.Account == account).ToDictionary(p => p.Key, p => p.Quantity);
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

    internal static bool HasFutuReconciliationEvidence(ImportData data, DateOnly begin, DateOnly end, string[] accounts,
        List<Match> matches, List<CashReconciliation> cashChecks, List<Issue> issues)
    {
        // 旧规范化快照继续使用其冻结的核对规则。
        if (!TaxImportProvenance.UsesFutuAutomaticProjection(data.ImportProvenance)) return false;

        var reports = data.Sources.Where(s => s.Purpose == "AnnualActivity" && s.Start <= begin && s.End >= end).ToArray();
        if (reports.Length == 0) return false;
        var trades = data.Trades.Where(t => t.Time.Year == end.Year).ToArray();
        var checkIds = data.TradeAmountChecks.Where(c => c.Status == "一致" && c.ReportedGross.HasValue && c.ReportedNet.HasValue)
            .Select(c => (c.Account, c.TradeId)).ToHashSet();
        var matchedQuantities = matches.ToLookup(m => (m.Key, m.SellId));
        var balances = cashChecks.Where(c => c.Start <= begin && c.End >= end).ToLookup(c => (c.Account, c.Currency));
        var expectedCash = trades.Select(t => (t.Key.Account, t.Key.Currency))
            .Concat(data.Cash.Where(c => c.Date.Year == end.Year).Select(c => (c.Account, c.Currency)))
            .Concat(data.Positions.Where(p => p.Date == end).Select(p => (p.Key.Account, p.Key.Currency)))
            .Concat(data.BeginningPositions.Where(p => p.Date == begin || p.Date == begin.AddDays(-1)).Select(p => (p.Key.Account, p.Key.Currency)))
            .Concat(data.AnnualIncome.Where(i => i.Year == end.Year).Select(i => (i.Account, i.Currency)))
            .Distinct().ToArray();
        foreach (var pair in expectedCash.Where(pair => !balances.Contains(pair)))
            issues.Add(new("FUTU_CASH_EVIDENCE", "缺少该币种的券商期初或期末现金余额。", pair.Account, Currency: pair.Currency));
        var accountsWithBalances = balances.Select(group => group.Key.Account).ToHashSet();
        foreach (var account in accounts.Where(a => !accountsWithBalances.Contains(a) && !expectedCash.Any(p => p.Account == a)))
            issues.Add(new("FUTU_CASH_EVIDENCE", "缺少该账户的券商资金总览余额。", account));

        return accounts.All(a => reports.Any(s => s.Account == a && s.HasTrades && s.HasCash && s.HasPositions) &&
                accountsWithBalances.Contains(a)) &&
            expectedCash.All(balances.Contains) &&
            cashChecks.All(c => c.Starting.HasValue && c.ReportedEnding.HasValue) &&
            trades.All(t => checkIds.Contains((t.Key.Account, t.Id)) &&
                (t.Side != "SELL" || matchedQuantities[(t.Key, t.Id)].Sum(m => m.Quantity) == t.Quantity)) &&
            !issues.Any(i => i.AffectsCost || i.Code is "UNSUPPORTED_ASSET" or "UNSUPPORTED_POSITION" or "POSITION_PERIOD" or
                "FUND_POSITION_EVIDENCE" or "MISSING_ACCOUNT_INCOME" or "INCOME_ACCOUNT_UNCONFIRMED" or "INCOMPLETE_PERIOD" or "OPENING_UNCONFIRMED");
    }
}
