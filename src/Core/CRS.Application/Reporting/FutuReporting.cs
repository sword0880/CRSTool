namespace CRS.Application;

public sealed record CapitalSummary(string Account, string Symbol, string Market, string Currency, int Count, decimal Gain, decimal GainCny);
public sealed record MovementSummary(string Account, string Symbol, string Currency, int Count, decimal Amount);

/// <summary>界面与 Excel 共用的富途辅助汇总，不修改原始事实或重新计算税额。</summary>
public static class FutuReporting
{
    /// <summary>按账户、证券、市场和原币汇总已确定的 FIFO 明细。</summary>
    public static List<CapitalSummary> Capital(CalculationResult result) => result.Matches.GroupBy(m => (m.Key.Account, m.Symbol, m.Key.Market, m.Key.Currency))
        .Select(g => new CapitalSummary(g.Key.Account, g.Key.Symbol, g.Key.Market, g.Key.Currency, g.Count(), g.Sum(m => m.Gain), g.Sum(m => m.GainCny))).ToList();
    /// <summary>仅提取入金方向的出入金，保留原币金额，不把证券卖出当入金。</summary>
    public static List<MovementSummary> Deposits(CalculationResult result) => result.FundMovements.Where(f => f.Direction == "In" && f.Type == "出入金")
        .GroupBy(f => (f.Account, f.Currency)).Select(g => new MovementSummary(g.Key.Account, "", g.Key.Currency, g.Count(), g.Sum(f => f.Amount))).ToList();
    /// <summary>汇总收到的公司行动派息，仅用于辅助核对，不重复计税，排除预扣税退回。</summary>
    public static List<MovementSummary> DividendReceipts(CalculationResult result) => result.FundMovements
        .Where(f => f.Direction == "In" && f.Type.Contains("公司行动") && !f.Description.Contains("withholding tax", StringComparison.OrdinalIgnoreCase))
        .GroupBy(f => (f.Account, f.Symbol, f.Currency)).Select(g => new MovementSummary(g.Key.Account, g.Key.Symbol, g.Key.Currency, g.Count(), g.Sum(f => f.Amount))).ToList();
}
