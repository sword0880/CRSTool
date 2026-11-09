using CRS.Domain.Performance;
using static CRS.Infrastructure.PerformanceBrokerMapping;

namespace CRS.Infrastructure;

/// <summary>从同一份归档原件补充汇总展示所需的券商市值。</summary>
internal static class FutuAccountOverviewValuations
{
    internal sealed record ReportValuation(ValuationSnapshot Snapshot, bool IsClosing, bool HasReportRows);

    internal static ReportValuation[] ReadAvailable(FutuTable table, IEnumerable<ValuationSnapshot> snapshots)
    {
        try { return Read(table, snapshots).ToArray(); }
        catch (CrsException) { return []; }
    }

    internal static ValuationSnapshot[] SelectForOverview(ValuationSnapshot[] valuations,
        IReadOnlyDictionary<string, ReportValuation> reports)
    {
        // 同账户同日优先使用有报表依据的期末记录，保留同类原件之间的冲突核对。
        var preferredPeriod = reports.Values.Where(v => v.HasReportRows)
            .GroupBy(v => (v.Snapshot.AccountId, v.Snapshot.Date))
            .ToDictionary(g => g.Key, g => g.Any(v => v.IsClosing));
        return valuations.Select(v =>
        {
            if (!reports.TryGetValue(v.Id, out var report)) return v;
            if (preferredPeriod.TryGetValue((v.AccountId, v.Date), out var closing) &&
                (!report.HasReportRows || report.IsClosing != closing)) return null;
            return report.Snapshot;
        }).OfType<ValuationSnapshot>().ToArray();
    }

    private static IEnumerable<ReportValuation> Read(FutuTable table, IEnumerable<ValuationSnapshot> snapshots)
    {
        var years = table.Read("账户信息", true, "年份")!.Select(r => FutuFields.Year(r["年份"])).Distinct().ToArray();
        if (years.Length != 1) yield break;
        var year = years[0];
        var positions = table.Read("持仓总览", false, "时期类型", "日期", "账户号码", "品类", "代码名称", "交易所/市场", "币种", "数量/面值") ?? [];
        var byPeriod = positions.Where(r => r["时期类型"] is "期初" or "期末").ToLookup(ReportPeriod);
        var balances = table.Read("资金总览", false, "时期类型", "日期", "账户号码", "币种", "金额") ?? [];
        var cashByPeriod = balances.Where(r => r["时期类型"] is "期初" or "期末").ToLookup(ReportPeriod);
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Date.Year != year && snapshot.Date.Year != year - 1) continue;
            var isClosing = snapshot.Date.Year == year;
            var period = (snapshot.AccountId, snapshot.Date, isClosing ? "期末" : "期初");
            var rows = byPeriod[period];
            var marketValues = rows.Where(r => r["品类"] == "证券")
                .ToLookup(r => (r["代码名称"].ToUpperInvariant(), r["交易所/市场"].ToUpperInvariant(),
                    FutuFields.Currency(r["币种"]), FutuFields.Money(r["数量/面值"], "数量/面值")));
            var cash = cashByPeriod[period].ToLookup(r => FutuFields.Currency(r["币种"]));
            var fundRows = rows.Where(r => r["品类"] == "基金").Select(ReadFundValue).ToArray();
            var funds = fundRows.Select(f => f.Component).OfType<ValuationComponent>().ToArray();
            var holdings = snapshot.Components.Select(c => c.Kind == "Cash" || c is { Kind: "Liability", Security: "", Market: "" }
                ? WithCashBalance(c, cash) : WithMarketValue(c, marketValues)).ToArray();
            var securityKeys = holdings.Where(h => h.Kind == "Security").Select(PositionKey).ToHashSet();
            // 空白基金市值按零处理。异常值和重复持仓仍须核对。
            var incomplete = fundRows.Any(f => f.Invalid) ||
                funds.GroupBy(PositionKey).Any(g => g.Count() > 1 || securityKeys.Contains(g.Key)) ||
                rows.Any(r => r["品类"] != "基金" &&
                    (r["品类"] != "证券" || FutuFields.Money(r["数量/面值"], "数量/面值") < 0));
            var valuation = snapshot with
            {
                Components = [.. holdings, .. funds],
                Quality = incomplete ? PerformanceQuality.InsufficientData : snapshot.Quality
            };
            yield return new(valuation, isClosing, rows.Any() || cash.Count > 0);
        }
    }

    private static (string AccountId, DateOnly Date, string Phase) ReportPeriod(FutuRow row)
        => (AccountId("FUTU", "FUTU:" + row["账户号码"]), FutuFields.Date(row["日期"]), row["时期类型"]);

    private static (string Security, string Market, string Currency) PositionKey(ValuationComponent component)
        => (component.Security, component.Market, component.Currency);

    private static (ValuationComponent? Component, bool Invalid) ReadFundValue(FutuRow row)
    {
        try
        {
            var amount = FutuFields.OptionalMoney(row["市值"], "基金市值") ?? 0;
            if (amount == 0) return (null, false);
            var quantity = ReadAmount(row, "数量/面值");
            var security = row["代码名称"].ToUpperInvariant();
            if (amount < 0 || quantity is null or < 0 || security.Length == 0) return (null, true);
            return (new("Security", FutuFields.Currency(row["币种"]), amount, security,
                row["交易所/市场"].ToUpperInvariant(), quantity.Value), false);
        }
        catch (CrsException) { return (null, true); }
    }

    private static ValuationComponent WithCashBalance(ValuationComponent component, ILookup<string, FutuRow> cash)
    {
        var rows = cash[component.Currency].Take(2).ToArray();
        // 富途原投影将结单负现金记作负债，汇总按原始符号展示并仍从净资产扣除。
        var amount = rows.Length == 0 ? (component.Kind == "Liability" ? -component.Amount : component.Amount)
            : rows.Length == 1 ? ReadAmount(rows[0], "金额") : null;
        return component with { Kind = "Cash", Amount = amount };
    }

    private static ValuationComponent WithMarketValue(ValuationComponent component,
        ILookup<(string Security, string Market, string Currency, decimal Quantity), FutuRow> marketValues)
    {
        if (component.Kind != "Security" || component.Amount.HasValue) return component;

        var rows = marketValues[(component.Security, component.Market, component.Currency, component.Quantity)].Take(2).ToArray();
        if (rows.Length != 1) return component;
        var value = ReadAmount(rows[0], "市值");
        return value is >= 0 ? component with { Amount = value } : component;
    }

    private static decimal? ReadAmount(FutuRow row, string field)
    {
        try { return FutuFields.OptionalMoney(row[field], field); }
        catch (CrsException) { return null; }
    }
}
