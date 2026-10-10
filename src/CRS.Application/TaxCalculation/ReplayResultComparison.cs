using System.Text.Json;

namespace CRS.Application;

/// <summary>复算比较金额、批次余差、来源、问题和状态；集合展示顺序不影响业务等价判断。</summary>
internal static class ReplayResultComparison
{
    public static void Verify(CalculationResult original, CalculationResult replay)
    {
        if (Describe(original) != Describe(replay))
            throw new CrsException("历史复算的金额、LOT、余差、来源、问题或状态与原结果不一致，未保存新任务。");
    }

    private static string Describe(CalculationResult r) => JsonSerializer.Serialize(new
    {
        r.Broker, r.Year, r.Summary, r.ImportedTradeCount,
        matches = Rows(r.Matches), lots = Rows(r.EndingLots), rounding = Rows(r.EndingRounding),
        issues = Rows(r.Issues), warnings = r.Warnings.Order(StringComparer.Ordinal),
        cash = Rows(r.CashChecks), pnl = Rows(r.PnlChecks), rates = Rows(r.Rates), sources = Rows(r.Sources),
        income = Rows(r.AnnualIncome), funds = Rows(r.FundMovements), amounts = Rows(r.TradeAmountChecks),
        events = Rows(r.SecurityMovements), r.Provisional, r.Reconciled, r.CarryEligible,
        r.OpeningZero, r.ScopeConfirmed, r.CalculationStatus, r.DataCompleteness, r.ReconciliationStatus,
        r.UsageLabel, r.TaxpayerScopeId, accounts = r.CoveredAccounts.Order(StringComparer.Ordinal),
        r.UnknownGainCount, r.EstimatedTopUpCny, r.Complete,
        taxIncome = r.TaxInputs is null ? null : Rows(r.TaxInputs.Income),
        taxPayments = r.TaxInputs is null ? null : Rows(r.TaxInputs.ForeignTax),
        taxVersion = r.TaxInputs?.Version, taxRule = r.TaxInputs?.RuleVersion, taxBroker = r.TaxInputs?.Broker
    });

    // 按序列化后的完整记录排序，避免不同文件枚举次序制造虚假的输出差异。
    private static string[] Rows<T>(IEnumerable<T> values) => values.Select(v => JsonSerializer.Serialize(v))
        .Order(StringComparer.Ordinal).ToArray();
}
