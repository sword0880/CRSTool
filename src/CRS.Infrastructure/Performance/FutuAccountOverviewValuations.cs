using CRS.Domain.Performance;
using static CRS.Infrastructure.PerformanceBrokerMapping;

namespace CRS.Infrastructure;

/// <summary>从同一份归档原件补充汇总展示所需的券商市值。</summary>
internal static class FutuAccountOverviewValuations
{
    internal static ValuationSnapshot[] ReadAvailable(FutuTable table, IEnumerable<ValuationSnapshot> snapshots)
    {
        try { return Read(table, snapshots).ToArray(); }
        catch (CrsException) { return []; }
    }

    private static IEnumerable<ValuationSnapshot> Read(FutuTable table, IEnumerable<ValuationSnapshot> snapshots)
    {
        var positions = table.Read("持仓总览", false, "时期类型", "日期", "账户号码", "品类", "代码名称", "交易所/市场", "币种", "数量/面值") ?? [];
        var byAccountDate = positions.Where(r => r["时期类型"] is "期初" or "期末")
            .ToLookup(AccountDate);
        var balances = table.Read("资金总览", false, "时期类型", "日期", "账户号码", "币种", "金额") ?? [];
        var cashByAccountDate = balances.Where(r => r["时期类型"] is "期初" or "期末").ToLookup(AccountDate);
        foreach (var snapshot in snapshots)
        {
            var rows = byAccountDate[(snapshot.AccountId, snapshot.Date)];
            var marketValues = rows.Where(r => r["品类"] == "证券")
                .ToLookup(r => (r["代码名称"].ToUpperInvariant(), r["交易所/市场"].ToUpperInvariant(),
                    FutuFields.Currency(r["币种"]), FutuFields.Money(r["数量/面值"], "数量/面值")));
            var cash = cashByAccountDate[(snapshot.AccountId, snapshot.Date)].ToLookup(r => FutuFields.Currency(r["币种"]));
            yield return snapshot with
            {
                Components = snapshot.Components.Select(c => c.Kind == "Cash" || c is { Kind: "Liability", Security: "", Market: "" }
                    ? WithCashBalance(c, cash) : WithMarketValue(c, marketValues)).ToArray(),
                Quality = rows.Any(r => r["品类"] != "证券" || FutuFields.Money(r["数量/面值"], "数量/面值") < 0)
                    ? PerformanceQuality.InsufficientData : snapshot.Quality
            };
        }
    }

    private static (string AccountId, DateOnly Date) AccountDate(FutuRow row)
        => (AccountId("FUTU", "FUTU:" + row["账户号码"]), FutuFields.Date(row["日期"]));

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
